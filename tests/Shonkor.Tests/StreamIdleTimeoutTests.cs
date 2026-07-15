// Licensed to Shonkor under the MIT License.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shonkor.Core.Models;
using Shonkor.Infrastructure.Services;

namespace Shonkor.Tests;

/// <summary>
/// The streaming answer is bounded by an idle timeout (#230) — the last unguarded variant of the broken-backend
/// class #225/#227 were about.
///
/// <para>
/// Under <c>ResponseHeadersRead</c>, <c>HttpClient.Timeout</c> stops applying once the headers are in, so body
/// reads are otherwise unbounded: a model that emits a few tokens and then wedges (a GPU hang, a paused
/// container) would leave the request spinning forever with no log line — the worst symptom of the three,
/// because there is not even a wrong answer to look at. These pin that a stall now fails within the idle window,
/// while a slow-but-alive model is left alone because the timer resets on every token.
/// </para>
/// </summary>
public class StreamIdleTimeoutTests
{
    private static OllamaSemanticAnalyzer Analyzer(string url, double idleSeconds)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SemanticAnalyzer:OllamaUrl"] = url,
            ["SemanticAnalyzer:StreamIdleTimeoutSeconds"] = idleSeconds.ToString(CultureInfo.InvariantCulture),
            // Far above the idle window, so it is unmistakably the idle guard that fires, not HttpClient.Timeout.
            ["SemanticAnalyzer:TimeoutSeconds"] = "100"
        }).Build();
        return OllamaClientFactory.CreateSemanticAnalyzer(config, NullLogger<OllamaSemanticAnalyzer>.Instance, new HttpClient());
    }

    private static GraphNode Node() => new()
    {
        Id = "A", Name = "A", Type = "Class", Content = "class A {}", FilePath = "src/A.cs"
    };

    [Fact]
    public async Task AStreamThatStallsAfterAToken_FailsWithinTheIdleTimeout_NotAnEternalSpinner()
    {
        // Headers + one token arrive, then the backend goes silent forever.
        using var backend = FakeOllamaBackend.ThatStreamsThenStalls("""{"response":"Hello ","done":false}""" + "\n");
        var analyzer = Analyzer(backend.Url, idleSeconds: 1);

        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<OllamaResponseException>(async () =>
        {
            await foreach (var chunk in analyzer.StreamRAGResponseAsync("how are tokens hashed?", [Node()]))
            {
                sb.Append(chunk);
            }
        });
        sw.Stop();

        Assert.Contains("Hello", sb.ToString(), StringComparison.Ordinal); // the token it DID send was delivered
        Assert.Contains("idle", ex.Message, StringComparison.OrdinalIgnoreCase); // ...then the stall was surfaced
        // The idle guard (1s) ended it — not the 100s HttpClient timeout, and above all not "never". Without the
        // guard this test would hang until the runner kills it.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"expected the ~1s idle guard to fire, but it took {sw.Elapsed}.");
    }

    [Fact]
    public async Task ASlowButAliveStream_IsNotKilled_TheIdleTimerResetsOnEveryToken()
    {
        // Five tokens 400 ms apart (each gap well under the 1s idle window), then done. The gaps total 1.6s —
        // MORE than the idle window — so a timer that did not reset per token would trip at 1s. Because it does
        // reset, every individual gap clears the window and the whole answer arrives.
        var lines = new[]
        {
            """{"response":"a ","done":false}""" + "\n",
            """{"response":"b ","done":false}""" + "\n",
            """{"response":"c ","done":false}""" + "\n",
            """{"response":"d ","done":false}""" + "\n",
            """{"response":"e","done":true}""" + "\n"
        };
        using var backend = FakeOllamaBackend.ThatDripsThenCompletes(lines, TimeSpan.FromMilliseconds(400));
        var analyzer = Analyzer(backend.Url, idleSeconds: 1);

        var sb = new StringBuilder();
        await foreach (var chunk in analyzer.StreamRAGResponseAsync("q", [Node()]))
        {
            sb.Append(chunk);
        }

        Assert.Contains("a b c d e", sb.ToString(), StringComparison.Ordinal); // completed in full, never killed
    }
}
