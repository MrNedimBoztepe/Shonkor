// Licensed to Shonkor under the MIT License.

using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shonkor.Core.Models;
using Shonkor.Infrastructure.Services;

namespace Shonkor.Tests;

/// <summary>
/// The streaming RAG path never replays an answer (#224) — the last of the three "safe by construction"
/// claims in the resilience code to be checked.
///
/// <para>
/// The other two: #215/#218 (the retry classifiers) were <b>broken</b>; #222 (deterministic failures are never
/// retried) <b>held</b>, but was protected twice over rather than once, so the word "structural" understated it.
/// This one is the last, and it is a third outcome again: <b>the claim is true, and the stated reason is not
/// why.</b>
/// </para>
///
/// <para><b>The claim, as written in the code:</b></para>
/// <blockquote>
/// No retry loop — a stream can't be safely restarted once bytes are on the wire; a failure after headers are
/// sent surfaces to the caller, which can only mark the partial answer.
/// </blockquote>
///
/// <para>
/// That sentence is true. It is just not what is protecting anything. <c>StreamRAGResponseAsync</c> sends with
/// <c>HttpCompletionOption.ResponseHeadersRead</c>, so a retry pipeline placed around that send could only ever
/// see failures from <i>before the headers arrived</i> — where nothing has been yielded and a restart is
/// perfectly safe. A failure <i>after</i> bytes are flowing happens once the send has already returned, and is
/// therefore unretriable <b>by construction</b>, exactly like the deterministic failures of #222 and the
/// mid-body deaths of #221. The absence of a pipeline is not what makes the stream safe; the completion option
/// is.
/// </para>
/// <para>
/// So these tests pin the property that is actually load-bearing — <b>a token is never emitted twice, and the
/// backend is never asked to generate the same answer again</b> — rather than the incidental one ("there is no
/// pipeline here"). A guard tied to the absence of a pipeline would fail the moment someone added a
/// <i>safe</i> pre-response retry, which is a change worth allowing.
/// </para>
/// </summary>
public class StreamingNoRetryTests
{
    private static OllamaSemanticAnalyzer Analyzer(string url) =>
        OllamaClientFactory.CreateSemanticAnalyzer(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SemanticAnalyzer:OllamaUrl"] = url,
                ["SemanticAnalyzer:TimeoutSeconds"] = "10"
            }).Build(),
            NullLogger<OllamaSemanticAnalyzer>.Instance);

    private static GraphNode Node() => new()
    {
        Id = "A", Name = "A", Type = "Class", Content = "class A {}", FilePath = "src/A.cs"
    };

    /// <summary>Drains the stream, tolerating the failure it is meant to end in.</summary>
    private static async Task<string> CollectAsync(OllamaSemanticAnalyzer analyzer)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            await foreach (var chunk in analyzer.StreamRAGResponseAsync("what does the graph do?", [Node()]))
            {
                sb.Append(chunk);
            }
        }
        catch
        {
            // The failure is the point of the fixture; what we assert is what reached the wire, and how often.
        }
        return sb.ToString();
    }

    /// <summary>
    /// <b>The property that matters.</b> The backend answers, streams real tokens, and then dies. If the stream
    /// were ever restarted, the caller would see those tokens <i>twice</i> — a duplicated paragraph presented
    /// as one answer, which is a correctness bug the reader cannot see. It must be generated once and delivered
    /// once.
    /// </summary>
    [Fact]
    public async Task WhenTheStreamDiesMidAnswer_TheTokensAlreadySentAreNotReplayed()
    {
        // Two real tokens, then a clean end of stream with no terminal done=true line.
        using var backend = FakeOllamaBackend.ThatAnswers(
            """{"response":"Tokens are hashed ","done":false}""" + "\n" +
            """{"response":"with SHA-256.","done":false}""" + "\n");

        var answer = await CollectAsync(Analyzer(backend.Url));

        // The generation happened once...
        Assert.Equal(1, backend.Requests);

        // ...and the tokens it produced appear exactly once in what the caller received.
        var occurrences = answer.Split("Tokens are hashed with SHA-256.").Length - 1;
        Assert.Equal(1, occurrences);

        // ...and the answer still announces its own incompleteness (#227), rather than reading as finished.
        Assert.Contains("incomplete", answer, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A backend that dies mid-BODY (mid-JSON, not on a line boundary) is the harsher version of the same
    /// thing: the send has long returned, so no pipeline could retry it even if one existed.
    /// </summary>
    [Fact]
    public async Task ABackendThatDiesMidBody_IsNotAskedToGenerateAgain()
    {
        using var backend = new MisbehavingBackend(MisbehavingBackend.Mode.DieMidBody);

        await CollectAsync(Analyzer(backend.Url));

        Assert.Equal(1, backend.Requests);
    }

    /// <summary>
    /// A 5xx: the backend refused before generating anything. Retrying this would be <i>safe</i> (nothing was
    /// streamed), and the blocking path deliberately does not retry it either — a 5xx mid-generation means the
    /// model already failed, and re-running a minutes-long generation on the off-chance is not a service to the
    /// caller. Pinned so the streaming path keeps agreeing with the blocking one about that.
    /// </summary>
    [Fact]
    public async Task AServerError_IsNotRetried_TheStreamingPathAgreesWithTheBlockingOne()
    {
        using var backend = new FakeOllamaBackend(HttpStatusCode.ServiceUnavailable);

        await CollectAsync(Analyzer(backend.Url));

        Assert.Equal(1, backend.Requests);
        Assert.NotEqual(OllamaResilience.BackgroundAttempts, backend.Requests); // ...and certainly not 3
    }

    /// <summary>
    /// The contrast that gives the above its meaning: the SAME backend, the SAME failure, on the enrichment
    /// path — retried. If this ever stops differing, the streaming path's distinct handling has no reason to
    /// exist and someone should say so out loud.
    /// </summary>
    [Fact]
    public async Task TheEnrichmentPath_OnTheSameServerError_DoesRetry_SoTheAsymmetryIsReal()
    {
        using var backend = new FakeOllamaBackend(HttpStatusCode.ServiceUnavailable);
        var analyzer = Analyzer(backend.Url);

        await Assert.ThrowsAnyAsync<Exception>(() => analyzer.AnalyzeNodeAsync(Node()));

        Assert.Equal(OllamaResilience.BackgroundAttempts, backend.Requests);
    }
}
