// Licensed to Shonkor under the MIT License.

using System.Net.Http;
using Polly;
using Polly.Retry;

namespace Shonkor.Infrastructure.Services;

/// <summary>
/// The Ollama HTTP resilience policy (#116), defined <b>once</b> and applied to every construction path.
///
/// <para>
/// <b>Why this exists at all.</b> Retry, exponential backoff and jitter used to be hand-written loops inside
/// <c>OllamaEmbeddingService</c> and <c>OllamaSemanticAnalyzer</c>. Polly (via
/// <c>Microsoft.Extensions.Http.Resilience</c>) is the canonical implementation of exactly that, and it is
/// maintained, battle-tested and configurable. Hand-rolling the mechanism was never the interesting part —
/// deciding <i>what counts as transient for Ollama</i> is, and that judgement stays ours
/// (<see cref="OllamaRetry"/>).
/// </para>
///
/// <para>
/// <b>Why the policy is applied at the call site, not in the typed-client registration.</b> The ticket
/// proposed the registration, and it cannot work — for two independent reasons:
/// </para>
/// <list type="number">
///   <item><b>It would only cover the web host.</b> <c>Shonkor.CLI</c> and <c>Shonkor.Bench</c> construct
///   <c>new HttpClient()</c> directly, so a DI-only policy would silently leave them with <b>no retry at
///   all</b> — and the CLI path is the <b>MCP stdio server</b>, the one agents actually use.</item>
///   <item><b>The two operations need different policies, on the same client.</b> Background work retries
///   transient failures; the blocking RAG path must retry a connection failure but <b>never a timeout</b>.
///   One handler on one <c>HttpClient</c> can only carry one policy, so the AC as written is unsatisfiable
///   alongside the RAG requirement it also states.</item>
/// </list>
/// <para>
/// So the pipelines live here and each call site selects the one it needs. The services still carry <b>no
/// retry loop, no backoff arithmetic, no jitter</b> — which is what the migration was for — and every
/// construction path behaves identically, because the policy travels with the service rather than the client.
/// </para>
///
/// <para>
/// <b>Two properties the old classifier had to enforce by rule are now true by construction:</b>
/// </para>
/// <list type="bullet">
///   <item>An <see cref="OllamaResponseException"/> (a 200 OK carrying an unusable payload) is raised by the
///   <i>service</i>, after the handler has already returned. The retry pipeline never sees it, so it cannot
///   retry it — no rule required.</item>
///   <item>A caller-triggered cancellation propagates out of the pipeline rather than being classified as a
///   failure. The old code had to distinguish "the caller cancelled" from "HttpClient timed out" by
///   inspecting the token, because both surface as <see cref="TaskCanceledException"/>.</item>
/// </list>
/// </summary>
public static class OllamaResilience
{
    /// <summary>
    /// <b>Do not also register a resilience handler on the <c>HttpClient</c>.</b> (#179)
    ///
    /// <para>
    /// The pipelines here wrap the <i>call</i>. A handler-level policy — e.g.
    /// <c>AddStandardResilienceHandler()</c> on the typed-client registration in <c>Shonkor.Web</c> — would sit
    /// <b>inside</b> them, so every attempt this pipeline makes would itself be retried by the handler:
    /// retries nested in retries, multiplying the attempt count and the wait against a backend that is already
    /// struggling. It is a natural mistake, because the typed-client registration is exactly where a reader
    /// expects the policy to live, and <c>AddStandardResilienceHandler</c> is right there in the same package.
    /// </para>
    /// <para>
    /// So it is not left to a comment: <c>OllamaResiliencePolicyPlacementTests</c> boots the real web host,
    /// points it at a failing backend and counts the HTTP attempts that actually reach it. Add a handler-level
    /// policy and the count multiplies past <see cref="BackgroundAttempts"/> and the test fails.
    /// </para>
    /// </summary>
    /// <remarks>Total attempts a background call makes against the backend, the first one included.</remarks>
    public static int BackgroundAttempts => 1 + BackgroundRetries;

    /// <summary>Total attempts a blocking (RAG) call makes against the backend, the first one included.</summary>
    public static int BlockingAttempts => 1 + BlockingRetries;

    /// <summary>Attempts after the first, for the background paths (enrichment, embedding).</summary>
    private const int BackgroundRetries = 2;

    /// <summary>
    /// The blocking RAG path gets exactly <b>one</b> retry, and only for a connection failure.
    /// <para>
    /// A generation request can legitimately run for minutes. Retrying a <i>timeout</i> there would double an
    /// already minutes-long wait while a human sits watching — so a timeout must fail fast, even though it is
    /// "transient" in every other sense. A backend that simply was not listening yet is cheap to retry.
    /// </para>
    /// </summary>
    private const int BlockingRetries = 1;

    /// <summary>Retry policy for background work: embeddings and enrichment. Transient → retry with backoff.</summary>
    public static ResiliencePipeline<HttpResponseMessage> Background { get; } = Build(BackgroundRetries, connectOnly: false);

    /// <summary>Retry policy for the blocking RAG path: one retry, connection failures only.</summary>
    public static ResiliencePipeline<HttpResponseMessage> Blocking { get; } = Build(BlockingRetries, connectOnly: true);

    private static ResiliencePipeline<HttpResponseMessage> Build(int retries, bool connectOnly) =>
        new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(RetryOptions(retries, connectOnly))
            .Build();

    private static RetryStrategyOptions<HttpResponseMessage> RetryOptions(int retries, bool connectOnly) => new()
    {
        MaxRetryAttempts = retries,
        BackoffType = DelayBackoffType.Exponential,
        UseJitter = true,                       // concurrent workers must not retry in lockstep
        Delay = TimeSpan.FromSeconds(1),
        // The domain judgement stays ours: OllamaRetry decides what "transient" means for THIS backend.
        // Polly owns the mechanism (when to wait, how long, how many times) — which is all we were
        // hand-rolling. This is the division of labour that was worth the dependency.
        ShouldHandle = args => ValueTask.FromResult(ShouldRetry(args.Outcome, connectOnly))
    };

    private static bool ShouldRetry(Outcome<HttpResponseMessage> outcome, bool connectOnly)
    {
        if (outcome.Exception is { } ex)
        {
            return connectOnly ? OllamaRetry.IsConnectError(ex) : OllamaRetry.IsTransient(ex);
        }
        // A response that arrived at all is not a connection failure, so the blocking path never retries it.
        return !connectOnly && outcome.Result is { } response && OllamaRetry.IsTransientStatus(response.StatusCode);
    }
}
