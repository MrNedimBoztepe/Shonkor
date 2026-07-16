// Licensed to Shonkor under the MIT License.

using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shonkor.Core.Models;
using Shonkor.Infrastructure.Services;

namespace Shonkor.Tests;

/// <summary>
/// What the retry classifiers see when a <b>real</b> backend fails, and what they decide (#218).
///
/// <para>
/// #215 established the method the hard way: the classifier keyed on a <see cref="SocketException"/> buried in
/// the exception chain, a real <c>HttpClient</c> timeout buries one there, and so the blocking RAG path retried
/// the single failure it exists never to retry — invisible, because the test that "covered" the rule invented
/// its own exception instead of provoking one. This class therefore provokes every failure over a **real
/// socket** and asserts on what actually comes out.
/// </para>
/// <para>
/// #218 asked whether <see cref="OllamaRetry.IsTransient"/> had the mirror flaw (it classifies on the
/// <i>outermost</i> exception only, so a wrapped transient failure could go unretried). <b>It does not</b> —
/// every real shape below arrives as an <c>HttpRequestException</c> or a <c>TaskCanceledException</c> at the
/// top, and all are correctly transient. The audit did, however, turn up a <i>second</i> bug in
/// <see cref="OllamaRetry.IsConnectError"/>, of the same family as #215. See
/// <see cref="MidBodyFailure_IsNotAConnectError_TheBackendAlreadyAnswered"/>.
/// </para>
/// </summary>
public class OllamaFailureShapeTests
{
    private static async Task<Exception> Provoke(string url, TimeSpan timeout)
    {
        using var client = new HttpClient { Timeout = timeout };
        return await Record.ExceptionAsync(() =>
            client.PostAsJsonAsync($"{url}/api/generate", new { prompt = "x" }))
            ?? throw new InvalidOperationException("expected the call to fail, but it succeeded");
    }

    private static string DeadUrl()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop(); // nothing is listening there now
        return $"http://127.0.0.1:{port}";
    }

    // ---- the classifier, against exceptions .NET really produces --------------------------------------

    [Fact]
    public async Task ConnectionRefused_IsAConnectError_SoTheRagPathMayCheaplyRetryIt()
    {
        var ex = await Provoke(DeadUrl(), TimeSpan.FromSeconds(5));

        Assert.True(OllamaRetry.IsConnectError(ex), "nothing was listening — we never reached a backend");
        Assert.True(OllamaRetry.IsTransient(ex));
    }

    /// <summary>
    /// DNS is the one failure that is <b>not</b> provoked over a real socket here, deliberately. Its behaviour
    /// is environment-dependent: a resolver that returns NXDOMAIN promptly yields
    /// <see cref="HttpRequestError.NameResolutionError"/>, while a resolver that blackholes the query instead
    /// hangs until the client's own timeout — which is a different (and also correctly classified) failure. A
    /// guard whose verdict depends on the machine's DNS is flaky, and a flaky guard is worse than none.
    /// <para>
    /// So the <i>error value</i> is asserted, and it is not invented: it was observed from a live DNS failure
    /// while auditing this file. Both shapes it can take are covered — this one, and the timeout above.
    /// </para>
    /// </summary>
    [Fact]
    public void DnsFailure_IsAConnectError_ForTheSameReason()
    {
        var ex = new HttpRequestException(HttpRequestError.NameResolutionError, "no such host", new SocketException());

        Assert.True(OllamaRetry.IsConnectError(ex), "the name never resolved — we never reached a backend");
        Assert.True(OllamaRetry.IsTransient(ex));
    }

    /// <summary>
    /// The #215 bug, pinned against a real socket rather than a hand-built exception.
    ///
    /// <para>
    /// <b>The first assertion below can pass for the wrong reason (#240), so the fixture is checked first.</b>
    /// #215's whole point was that a real <c>HttpClient</c> timeout <i>buries a <see cref="SocketException"/></i>
    /// at the bottom of the chain, which the old chain-scan then mistook for a connection failure. Whether that
    /// <c>SocketException</c> is actually there depends on how the platform tears the socket down on
    /// cancellation. If a platform produced a bare <c>TaskCanceledException</c> with no socket error inside,
    /// <c>IsConnectError</c> would return <c>false</c> <b>without ever reaching the code path #215 broke</b> —
    /// a green guard, guarding nothing.
    /// </para>
    /// <para>
    /// So we assert that the provoked exception still has the #215 <i>shape</i> before asserting the verdict on
    /// it. If a future runtime stops burying the socket error, this fails loudly and says so, instead of
    /// quietly becoming decorative.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ATimeout_IsNotAConnectError_ButIsStillTransient()
    {
        using var hung = FakeOllamaBackend.ThatNeverResponds();

        var ex = await Provoke(hung.Url, TimeSpan.FromSeconds(2));

        // The fixture must still reproduce the shape the bug lived in, or the assertions below prove nothing.
        var hasBuriedSocketError = false;
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is SocketException) { hasBuriedSocketError = true; break; }
        }
        Assert.True(hasBuriedSocketError,
            $"a real HttpClient timeout is supposed to bury a SocketException in its chain — that is the whole " +
            $"trap #215 fell into. This one did not ({string.Join(" -> ", Chain(ex))}), so the assertions below " +
            $"would pass without exercising the classifier at all. The guard has lost its teeth: re-derive it.");

        Assert.False(OllamaRetry.IsConnectError(ex), "we DID reach the backend — it just never answered");
        Assert.True(OllamaRetry.IsTransient(ex), "background work should still retry a timeout");
    }

    private static IEnumerable<string> Chain(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException) yield return e.GetType().Name;
    }

    /// <summary>
    /// <b>The lesson #221 had to learn the hard way.</b> #218 asserted here that a mid-body death is
    /// <i>not</i> an <c>IsConnectError</c> — and that assertion passed on Windows and <b>failed on Linux</b> the
    /// first time CI ran on both (#209).
    /// <para>
    /// The reason is that .NET does not classify these portably. A <c>200 OK</c> that dies half-way reports
    /// <c>HttpRequestError.Unknown</c> on Windows but <c>ConnectionError</c> on Linux — identical to a refused
    /// connection. So the exception simply <b>cannot</b> tell us whether a response arrived, and #218's fix did
    /// nothing at all on the platform this ships on.
    /// </para>
    /// <para>
    /// This test therefore no longer asks the classifier a question it cannot portably answer. It asserts the
    /// thing that actually matters and <i>is</i> portable: <b>the RAG path does not re-run a generation that
    /// already started</b> — which #221 made true by construction, by reading only the headers inside the retry
    /// pipeline. See <c>OllamaBlockingPolicyTests</c> for the request-count guard; here we only pin the shape.
    /// </para>
    /// </summary>
    [Fact]
    public async Task MidBodyFailure_IsTransient_ButItsConnectErrorVerdictIsNotPortable_SoNothingReliesOnIt()
    {
        using var backend = new MisbehavingBackend(MisbehavingBackend.Mode.DieMidBody);

        var ex = await Provoke(backend.Url, TimeSpan.FromSeconds(5));

        // Background work retries it on every platform — a dropped body is worth another attempt when nobody
        // is waiting, and this verdict IS portable.
        Assert.True(OllamaRetry.IsTransient(ex));

        // IsConnectError's answer here is platform-dependent (Unknown on Windows, ConnectionError on Linux),
        // which is exactly why the blocking path no longer depends on it for this case: the body is read
        // OUTSIDE the retry pipeline, so a mid-body death cannot be retried whatever the classifier thinks.
        // Asserting a verdict either way would just re-pin a platform accident.
    }

    [Fact]
    public async Task AResetBeforeAnyResponse_IsTransient_AndReachesTheClassifierAsATransportFailure()
    {
        using var backend = new MisbehavingBackend(MisbehavingBackend.Mode.ResetBeforeResponding);

        var ex = await Provoke(backend.Url, TimeSpan.FromSeconds(5));

        Assert.True(OllamaRetry.IsTransient(ex));
        // Same caveat as above: whether .NET calls this ConnectionError or Unknown depends on the platform.
        // Nothing depends on that any more.
    }

    /// <summary>
    /// #218's actual question, answered: every failure a real backend produces reaches
    /// <see cref="OllamaRetry.IsTransient"/> as an <c>HttpRequestException</c> or a
    /// <c>TaskCanceledException</c> at the <b>top</b> of the chain, so classifying on the outermost type — the
    /// thing the ticket suspected — does not miss any of them. No wrapped transient failure goes unretried.
    /// </summary>
    [Fact]
    public async Task EveryRealTransportFailure_IsRetryableByTheBackgroundPath_NoneAreMissedByTheOutermostSwitch()
    {
        using var hung = FakeOllamaBackend.ThatNeverResponds();
        using var midBody = new MisbehavingBackend(MisbehavingBackend.Mode.DieMidBody);
        using var reset = new MisbehavingBackend(MisbehavingBackend.Mode.ResetBeforeResponding);

        var failures = new[]
        {
            await Provoke(DeadUrl(), TimeSpan.FromSeconds(5)),
            await Provoke(hung.Url, TimeSpan.FromSeconds(2)),
            await Provoke(midBody.Url, TimeSpan.FromSeconds(5)),
            await Provoke(reset.Url, TimeSpan.FromSeconds(5))
        };

        Assert.All(failures, ex => Assert.True(OllamaRetry.IsTransient(ex),
            $"a real transport failure went unretried by background work: {ex.GetType().Name}"));
    }

    // ---- end to end: what the two paths actually do about it -------------------------------------------

    [Fact]
    public async Task TheRagPath_WhenTheGenerationDiesMidBody_DoesNotReRunIt()
    {
        using var backend = new MisbehavingBackend(MisbehavingBackend.Mode.DieMidBody);
        var analyzer = Analyzer(backend.Url);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            analyzer.GenerateRAGResponseAsync("what does the graph do?", [Node("A")]));

        Assert.Equal(1, backend.Requests); // was 2 before the fix — a second multi-minute generation
    }

    [Fact]
    public async Task TheEnrichmentPath_OnTheSameFailure_DoesRetryIt_BecauseNobodyIsWaiting()
    {
        using var backend = new MisbehavingBackend(MisbehavingBackend.Mode.DieMidBody);
        var analyzer = Analyzer(backend.Url);

        await Assert.ThrowsAnyAsync<Exception>(() => analyzer.AnalyzeNodeAsync(Node("A")));

        Assert.Equal(OllamaResilience.BackgroundAttempts, backend.Requests);
    }

    /// <summary>
    /// The OTHER background path, on the same failure, must behave the same (#234).
    ///
    /// <para>
    /// Embedding is a separate service with its own send, so the asymmetry above only covers half of it: switch
    /// <c>OllamaEmbeddingService</c> alone to <c>ResponseHeadersRead</c> "for consistency with the RAG path" and
    /// indexing would quietly stop retrying every dropped body, while the enrichment test above stayed green.
    /// <c>OllamaCompletionOptionContractTests</c> catches that change in the source; this catches it in the
    /// OUTCOME, which is what the codebase prefers to pin — it fails only when the property genuinely breaks,
    /// not when the code is merely rearranged.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheEmbeddingPath_OnTheSameFailure_AlsoRetriesIt_TheBackgroundRuleIsNotJustEnrichments()
    {
        using var backend = new MisbehavingBackend(MisbehavingBackend.Mode.DieMidBody);
        var embedding = Embedding(backend.Url);

        await Assert.ThrowsAnyAsync<Exception>(() => embedding.GenerateEmbeddingAsync("hello"));

        Assert.Equal(OllamaResilience.BackgroundAttempts, backend.Requests);
    }

    private static OllamaSemanticAnalyzer Analyzer(string url) =>
        OllamaClientFactory.CreateSemanticAnalyzer(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SemanticAnalyzer:OllamaUrl"] = url,
                ["SemanticAnalyzer:TimeoutSeconds"] = "10"
            }).Build(),
            NullLogger<OllamaSemanticAnalyzer>.Instance);

    private static OllamaEmbeddingService Embedding(string url) =>
        OllamaClientFactory.CreateEmbeddingService(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EmbeddingService:OllamaUrl"] = url,
                ["EmbeddingService:TimeoutSeconds"] = "10"
            }).Build(),
            NullLogger<OllamaEmbeddingService>.Instance);

    private static GraphNode Node(string id) => new()
    {
        Id = id, Name = id, Type = "Class", Content = $"class {id} {{}}", FilePath = $"src/{id}.cs"
    };
}
