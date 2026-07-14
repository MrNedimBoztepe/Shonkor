// Licensed to Shonkor under the MIT License.

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Shonkor.Core.Models;
using Shonkor.Infrastructure.Services;

namespace Shonkor.Tests;

/// <summary>
/// The blocking RAG path must <b>never retry a timeout</b> — the rule <see cref="OllamaResilience.Blocking"/>
/// exists for, and the one nothing pinned (#213).
///
/// <para>
/// It is deliberately counter-intuitive, which is exactly why it needs a test. A timeout is "transient" in
/// every ordinary sense, and the background pipeline <i>does</i> retry it. But a RAG generation can legitimately
/// run for minutes with a human watching, so retrying a timeout there <b>doubles an already minutes-long wait</b>
/// for an answer that is probably not coming. It must fail fast. A backend that was merely not listening yet is
/// a different story — cheap to retry, so a connection failure gets exactly one retry.
/// </para>
/// <para>
/// This rule is the load-bearing reason the policy cannot live on the <c>HttpClient</c>'s handler: the
/// background path and the RAG path share one client, and one handler can carry only one policy. If the two
/// pipelines ever stop differing, that justification quietly evaporates — and #179's placement guard would
/// still pass, because it only ever measured the background path. So the difference itself is asserted here.
/// </para>
/// </summary>
public class OllamaBlockingPolicyTests
{
    // ---- the pipelines, exercised directly -----------------------------------------------------------
    //
    // A REAL HttpClient timeout cannot be driven end-to-end: OllamaSemanticAnalyzer hard-codes
    // HttpClient.Timeout to 2 minutes, so a test that waits for one would take 2 minutes (filed as a
    // follow-up). The pipeline is where the rule actually lives, so the rule is asserted there — with the
    // exception shape HttpClient really raises on timeout (TaskCanceledException with no cancelled caller
    // token), not a stand-in.

    /// <summary>Runs <paramref name="pipeline"/> against a callback that always throws, and counts the attempts.</summary>
    private static async Task<int> AttemptsUntilFailure(
        ResiliencePipeline<HttpResponseMessage> pipeline, Func<Exception> failure)
    {
        var attempts = 0;
        Func<CancellationToken, ValueTask<HttpResponseMessage>> alwaysFails = _ =>
        {
            attempts++;
            throw failure();
        };

        await Assert.ThrowsAnyAsync<Exception>(
            () => pipeline.ExecuteAsync(alwaysFails, CancellationToken.None).AsTask());
        return attempts;
    }

    /// <summary>
    /// What <see cref="HttpClient"/> throws when ITS timeout elapses — the <b>real</b> chain, reproduced from a
    /// live socket timeout.
    /// <para>
    /// This used to be a bare <c>TaskCanceledException</c>, and that was the bug (#215): tearing the connection
    /// down on timeout buries a <see cref="SocketException"/> at the bottom of the chain, and
    /// <c>IsConnectError</c> scanned for exactly that — so it called a timeout a connection failure and the
    /// blocking path retried it. The simplified fixture made the guard pass while the rule was broken in
    /// production. A test fixture that is easier than reality is not a test.
    /// </para>
    /// </summary>
    private static Exception ClientTimeout() =>
        new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 2 seconds elapsing.",
            new TimeoutException("The operation was canceled.",
                new TaskCanceledException("The operation was canceled.",
                    new IOException("Unable to read data from the transport connection.",
                        new SocketException()))));

    /// <summary>What a refused connection / DNS failure looks like: HttpRequestException with no status.</summary>
    private static Exception ConnectFailure() =>
        new HttpRequestException(HttpRequestError.ConnectionError, "connection refused", new SocketException());

    [Fact]
    public async Task Blocking_DoesNotRetryATimeout_BecauseAHumanIsWaiting()
    {
        var attempts = await AttemptsUntilFailure(OllamaResilience.Blocking, ClientTimeout);

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Blocking_DoesRetryAConnectionFailure_BecauseThatIsCheapAndLikelyFixable()
    {
        var attempts = await AttemptsUntilFailure(OllamaResilience.Blocking, ConnectFailure);

        Assert.Equal(OllamaResilience.BlockingAttempts, attempts); // 1 initial + 1 retry
        Assert.True(attempts > 1, "a backend that simply was not listening yet must be retried once");
    }

    [Fact]
    public async Task Background_DoesRetryATimeout_WhichIsPreciselyTheDifference()
    {
        // Same failure, same shared HttpClient in production — opposite decision. This asymmetry is the whole
        // reason the policy lives at the call site rather than on the client's handler.
        var attempts = await AttemptsUntilFailure(OllamaResilience.Background, ClientTimeout);

        Assert.Equal(OllamaResilience.BackgroundAttempts, attempts);
        Assert.True(attempts > 1);
    }

    [Fact]
    public async Task TheTwoPipelines_ActuallyDiffer_OrTheCallSitePlacementHasNoJustification()
    {
        var blocking = await AttemptsUntilFailure(OllamaResilience.Blocking, ClientTimeout);
        var background = await AttemptsUntilFailure(OllamaResilience.Background, ClientTimeout);

        Assert.True(blocking < background,
            "the background and RAG paths share one HttpClient and MUST want different things from it — if they " +
            "ever agree, a single handler-level policy would serve both and the call-site placement (#176/#179) " +
            "loses its reason to exist.");
    }

    // ---- end-to-end: the real RAG call against a real socket ------------------------------------------

    [Fact]
    public async Task TheRagPath_DoesNotRetryAServerError_SoOneRequestReachesTheBackend()
    {
        // 503 IS retried by the background pipeline (#179 measured 3 requests for an embedding). The RAG path
        // must not retry it: a 5xx mid-generation means the model already failed, and re-running a
        // minutes-long generation on the off-chance is not a service to the caller.
        using var backend = new FakeOllamaBackend(HttpStatusCode.ServiceUnavailable);
        var analyzer = Analyzer(backend.Url);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            analyzer.GenerateRAGResponseAsync("what does the graph do?", [Node("A")]));

        Assert.Equal(1, backend.Requests);
    }

    [Fact]
    public async Task TheEnrichmentPath_OnTheSameClient_DoesRetryTheSameServerError()
    {
        // The mirror image, proving the asymmetry is real in the shipped services and not just in the
        // pipelines: the SAME backend, the SAME failure, the SAME analyzer instance — retried here.
        using var backend = new FakeOllamaBackend(HttpStatusCode.ServiceUnavailable);
        var analyzer = Analyzer(backend.Url);

        await Assert.ThrowsAnyAsync<Exception>(() => analyzer.AnalyzeNodeAsync(Node("A")));

        Assert.Equal(OllamaResilience.BackgroundAttempts, backend.Requests);
    }

    private static OllamaSemanticAnalyzer Analyzer(string url)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SemanticAnalyzer:OllamaUrl"] = url })
            .Build();
        return OllamaClientFactory.CreateSemanticAnalyzer(config, NullLogger<OllamaSemanticAnalyzer>.Instance);
    }

    private static GraphNode Node(string id) => new()
    {
        Id = id, Name = id, Type = "Class", Content = $"class {id} {{}}", FilePath = $"src/{id}.cs"
    };
}
