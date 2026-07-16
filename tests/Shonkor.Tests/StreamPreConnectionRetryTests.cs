// Licensed to Shonkor under the MIT License.

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shonkor.Core.Models;
using Shonkor.Infrastructure.Services;

namespace Shonkor.Tests;

/// <summary>
/// The streaming RAG path recovers a connection that was merely not listening yet (#243).
///
/// <para>
/// #224 established <i>why</i> the stream is safe to retry: the send uses <c>ResponseHeadersRead</c>, so any
/// failure a retry pipeline can see happened before a single byte was yielded, and restarting is
/// indistinguishable from a first attempt. This acts on that. Before it, a user who asked a question while
/// Ollama was still starting up got their streamed answer failed outright, while the blocking path on the same
/// failure retried once and usually succeeded — the user-facing path was the least resilient of the three.
/// </para>
/// <para>
/// The property #224 protects is untouched, and <c>StreamingNoRetryTests</c> still passes unchanged: a 5xx is
/// not retried (it is a response, thrown by <c>EnsureSuccessStatusCode</c> outside the pipeline), and a
/// mid-stream failure never replays a token. This only adds recovery for a genuine pre-connection failure.
/// </para>
/// <para>
/// <b>The connect failure is injected, not raced.</b> A <c>DelegatingHandler</c> fails the first send with the
/// real exception .NET raises for a refused connection, then forwards to a live backend. That is deterministic
/// and identical on both platforms — unlike a socket that goes live on a timer, which would be a thread-pool
/// timing race of exactly the kind #246 was about.
/// </para>
/// </summary>
public class StreamPreConnectionRetryTests
{
    /// <summary>Fails the first <c>SendAsync</c> with a genuine connection error, then delegates to a real backend.</summary>
    private sealed class RefuseFirstThenForward : DelegatingHandler
    {
        private int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);
        public int FailCount { get; init; } = 1;

        public RefuseFirstThenForward() : base(new SocketsHttpHandler()) { }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref _attempts);
            if (n <= FailCount)
            {
                // Exactly what a refused connection looks like: HttpRequestError.ConnectionError over a
                // SocketException. IsConnectError classifies this as retryable on every platform.
                throw new HttpRequestException(HttpRequestError.ConnectionError, "connection refused (simulated)", new SocketException());
            }
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static OllamaSemanticAnalyzer Analyzer(string url, HttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        return OllamaClientFactory.CreateSemanticAnalyzer(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SemanticAnalyzer:OllamaUrl"] = url
            }).Build(),
            NullLogger<OllamaSemanticAnalyzer>.Instance,
            client);
    }

    private static GraphNode Node() => new()
    {
        Id = "A", Name = "A", Type = "Class", Content = "class A {}", FilePath = "src/A.cs"
    };

    private static async Task<string> CollectAsync(OllamaSemanticAnalyzer analyzer)
    {
        var sb = new System.Text.StringBuilder();
        await foreach (var ev in analyzer.StreamRAGResponseAsync("how are tokens hashed?", [Node()]))
        {
            sb.Append(ev.Token);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The behaviour #243 adds: the first attempt is refused (backend not up yet), the retry connects, and the
    /// answer arrives. The backend is hit <b>once</b> — only the successful attempt reached it — and the model
    /// was asked to generate exactly once, so nothing is replayed.
    /// </summary>
    [Fact]
    public async Task ARefusedConnection_IsRetried_AndTheAnswerArrives()
    {
        using var backend = FakeOllamaBackend.ThatAnswers(
            """{"response":"Tokens are hashed with SHA-256.","done":true}""" + "\n");
        var handler = new RefuseFirstThenForward();

        var answer = await CollectAsync(Analyzer(backend.Url, handler));

        Assert.Contains("Tokens are hashed with SHA-256.", answer, StringComparison.Ordinal);
        Assert.Equal(2, handler.Attempts);   // first refused, second succeeded
        Assert.Equal(1, backend.Requests);   // only the successful attempt reached the backend — no replay
    }

    /// <summary>
    /// The retry budget is bounded, exactly like the blocking path: one retry, not endless. A connection that
    /// stays refused fails after <see cref="OllamaResilience.BlockingAttempts"/> attempts rather than hanging
    /// or hammering.
    /// </summary>
    [Fact]
    public async Task AConnectionThatStaysRefused_FailsAfterExactlyTheBlockingBudget()
    {
        using var backend = FakeOllamaBackend.ThatAnswers("""{"response":"unused","done":true}""" + "\n");
        var handler = new RefuseFirstThenForward { FailCount = 99 }; // never recovers

        await Assert.ThrowsAnyAsync<Exception>(() => CollectAsync(Analyzer(backend.Url, handler)));

        Assert.Equal(OllamaResilience.BlockingAttempts, handler.Attempts); // 1 initial + 1 retry
        Assert.Equal(0, backend.Requests); // it never reached a live backend
    }
}
