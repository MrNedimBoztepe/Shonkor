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
        // 22 tokens 150 ms apart, then done. Two properties have to hold at once, and the margins are chosen so
        // a loaded CI runner cannot flip either:
        //   * every individual gap (150 ms) is FAR under the 3s idle window — it would have to stretch ~20x to
        //     trip the guard. (An earlier version used 400 ms against a 1s window; a 2-core Windows runner
        //     stretched one gap past 1s and failed the build. Runner jitter is the thing to design against.)
        //   * the gaps TOTAL 3.15s — more than the idle window — so a timer that accumulated instead of
        //     resetting would fire at 3s and kill this stream. It does not, so the whole answer arrives.
        // Note a slow runner only makes the second property MORE true (bigger total), so slowness cannot mask a
        // regression here; it can only threaten the per-gap margin, which is why that one is so generous.
        var lines = new List<string>();
        for (var i = 0; i < 21; i++) lines.Add("{\"response\":\"t" + i + " \",\"done\":false}\n");
        lines.Add("{\"response\":\"end\",\"done\":true}\n");

        using var backend = FakeOllamaBackend.ThatDripsThenCompletes(lines, TimeSpan.FromMilliseconds(150));
        var analyzer = Analyzer(backend.Url, idleSeconds: 3);

        var sb = new StringBuilder();
        await foreach (var chunk in analyzer.StreamRAGResponseAsync("q", [Node()]))
        {
            sb.Append(chunk);
        }

        // Every token, in order, plus the terminal one: the stream ran to completion and was never killed.
        var expected = string.Concat(Enumerable.Range(0, 21).Select(i => $"t{i} ")) + "end";
        Assert.Contains(expected, sb.ToString(), StringComparison.Ordinal);
    }
}
