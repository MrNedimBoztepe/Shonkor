// Licensed to Shonkor under the MIT License.

using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;

namespace Shonkor.Tests;

/// <summary>
/// The Ollama retry policy lives at the CALL SITE, and must not ALSO be attached to the HttpClient (#179).
///
/// <para>
/// After #176 the policy sits in <see cref="OllamaResilience"/> and is applied by the services, because it has
/// to cover the CLI and the bench as well — they build their own <c>HttpClient</c>, so a DI-only policy would
/// leave the MCP stdio server with no retry at all. That leaves <c>Shonkor.Web</c>'s typed-client registration
/// looking bare:
/// </para>
/// <code>
/// builder.Services.AddHttpClient&lt;IEmbeddingService, OllamaEmbeddingService&gt;();
/// </code>
/// <para>
/// which is precisely where a reader expects the policy to be, with <c>AddStandardResilienceHandler()</c> one
/// line away in the same package. Adding it would be a <b>bug</b>: a handler-level policy sits <i>inside</i>
/// the call-site pipeline, so every attempt the pipeline makes gets retried again by the handler — retries
/// nested in retries, multiplying the attempts and the wait against a backend that is already failing.
/// </para>
/// <para>
/// #179 called a test for this "awkward, because it means testing an absence". It doesn't: the absence has a
/// <b>behavioural</b> signature. Point the real web host at a backend that always fails and count the requests
/// that actually arrive. One policy → exactly <see cref="OllamaResilience.BackgroundAttempts"/>. Two nested
/// policies → several times that. So this asserts the attempt budget, not the shape of the DI graph.
/// </para>
/// </summary>
public class OllamaResiliencePolicyPlacementTests
{
    /// <summary>A stand-in Ollama that always fails transiently (503) and counts what reached it.</summary>
    private sealed class FailingBackend : IDisposable
    {
        private readonly HttpListener _listener = new();
        private int _requests;

        public string Url { get; }
        public int Requests => Volatile.Read(ref _requests);

        public FailingBackend()
        {
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add($"{Url}/");
            _listener.Start();
            _ = Task.Run(Serve);
        }

        private async Task Serve()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; } // listener stopped
                Interlocked.Increment(ref _requests);
                // 503 is transient by OllamaRetry's rules, so the call-site pipeline WILL retry it — which is
                // exactly what makes the attempt count a readable signal.
                ctx.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                ctx.Response.Close();
            }
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public void Dispose() { _listener.Close(); }
    }

    /// <summary>The real web host — the registration under test — pointed at <paramref name="ollamaUrl"/>.</summary>
    private sealed class AppFactory(string ollamaUrl, string workspace) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(workspace);
            builder.UseEnvironment("Production");
            builder.UseSetting("EmbeddingService:OllamaUrl", ollamaUrl);

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ProjectManager>();
                services.AddSingleton(_ => new ProjectManager(workspace));

                // Drop the background workers: they would make their own Ollama calls and pollute the count.
                // The typed-client registrations — the thing under test — are deliberately left untouched.
                foreach (var hosted in services.Where(d => d.ServiceType == typeof(IHostedService)).ToList())
                {
                    services.Remove(hosted);
                }
            });
        }
    }

    [Fact]
    public async Task TheWebHost_MakesExactlyTheCallSitePolicysAttempts_SoNoSecondPolicyIsNestedOnTheHttpClient()
    {
        using var backend = new FailingBackend();
        var workspace = Path.Combine(Path.GetTempPath(), $"shonkor_resil_{Guid.NewGuid():N}");
        await using var factory = new AppFactory(backend.Url, workspace);

        // Resolve the embedding service exactly as the app does — through the typed-client registration.
        var embedding = factory.Services.GetRequiredService<IEmbeddingService>();

        // The backend always 503s, so this exhausts the retry policy and then throws. The THROW is not the
        // point; the number of attempts it took to get there is.
        await Assert.ThrowsAnyAsync<Exception>(() => embedding.GenerateEmbeddingAsync("hello"));

        Assert.Equal(OllamaResilience.BackgroundAttempts, backend.Requests);
    }

    /// <summary>
    /// The same property stated from the other side: the attempt budget is what <see cref="OllamaResilience"/>
    /// says it is, so the test above is comparing against the policy rather than a number someone typed twice.
    /// </summary>
    [Fact]
    public void TheAttemptBudget_IsDerivedFromThePolicy_NotHardcoded()
    {
        Assert.Equal(3, OllamaResilience.BackgroundAttempts);  // 1 initial + 2 retries
        Assert.Equal(2, OllamaResilience.BlockingAttempts);    // 1 initial + 1 retry (connect failures only)
        Assert.True(OllamaResilience.BlockingAttempts < OllamaResilience.BackgroundAttempts,
            "the blocking RAG path must be the more impatient of the two — a human is waiting on it");
    }
}
