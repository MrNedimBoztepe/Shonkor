// Licensed to Shonkor under the MIT License.

using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shonkor.Core.Models;
using Shonkor.Infrastructure.Services;

namespace Shonkor.Tests;

/// <summary>
/// The blocking <c>/api/ask</c> body read is bounded (#266) — the twin of the streaming idle timeout (#230).
///
/// <para>
/// <c>GenerateRAGResponseAsync</c> sends under <c>ResponseHeadersRead</c> and then reads the JSON body with
/// <c>ReadFromJsonAsync</c>. Once the headers are in, <c>HttpClient.Timeout</c> no longer applies and that read
/// sits outside the resilience pipeline, so a backend that flushes headers and then stalls before the body
/// would hang the request forever — the same eternal-spinner symptom, on the non-stream path. The body read
/// now shares the headers phase's budget (the effective <c>HttpClient.Timeout</c>) and fails instead of hanging.
/// </para>
/// </summary>
public class BlockingBodyReadTimeoutTests
{
    private static OllamaSemanticAnalyzer Analyzer(string url, double timeoutSeconds)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SemanticAnalyzer:OllamaUrl"] = url,
            ["SemanticAnalyzer:TimeoutSeconds"] = timeoutSeconds.ToString(CultureInfo.InvariantCulture)
        }).Build();
        return OllamaClientFactory.CreateSemanticAnalyzer(config, NullLogger<OllamaSemanticAnalyzer>.Instance, new HttpClient());
    }

    private static GraphNode Node() => new()
    {
        Id = "A", Name = "A", Type = "Class", Content = "class A {}", FilePath = "src/A.cs"
    };

    [Fact]
    public async Task AResponseThatSendsHeadersThenStallsTheBody_FailsWithinTheTimeout_NotForever()
    {
        // 200 + headers + a partial, incomplete JSON body, then the connection is held open with no more bytes —
        // so the body read blocks waiting for a completion that never comes.
        //
        // 3s, not 1s: the SAME budget also bounds the send, so the headers must arrive inside it. The backend
        // writes them immediately (well under 100ms), but a loaded CI runner can stretch a first request badly —
        // and if the send timed out instead, this would throw TaskCanceledException and the assertion below
        // would fail for the wrong reason. 3s leaves ~30x headroom while still proving the read is bounded.
        using var backend = FakeOllamaBackend.ThatStreamsThenStalls("{\"response\":\"half an ans");
        var analyzer = Analyzer(backend.Url, timeoutSeconds: 3);

        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<OllamaResponseException>(
            () => analyzer.GenerateRAGResponseAsync("how are tokens hashed?", new[] { Node() }));
        sw.Stop();

        Assert.Contains("wedged", ex.Message, StringComparison.OrdinalIgnoreCase);
        // The ~1s read cap ended it — not "never". Without the bound this test would hang until the runner kills it.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"expected the ~1s read cap to fire, but it took {sw.Elapsed}.");
    }
}
