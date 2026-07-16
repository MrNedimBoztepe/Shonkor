// Licensed to Shonkor under the MIT License.

using System;
using System.IO;
using System.Net.Http;
using Microsoft.Data.Sqlite;
using Shonkor.Infrastructure.Services;

namespace Shonkor.Web;

/// <summary>
/// Stable, machine-readable identities for the ways a RAG request can fail (#228) — the HTTP API's counterpart
/// to <c>McpErrorCode</c> (#120).
///
/// <para>
/// #225 made a broken backend surface as a failure instead of as an answer, and <c>EndpointHelpers.Fail</c>
/// keeps the client message generic on purpose, so internal paths and SQL never leak. Both are right, and
/// together they left every RAG failure looking identical from outside: an operator reading "Failed to generate
/// RAG response." cannot tell "Ollama is not running" from "your model returns garbage" from "raise the
/// timeout" — remedies that have nothing to do with each other. The code conveys the CLASS of failure without
/// conveying any detail; the human message stays alongside, unconstrained.
/// </para>
/// <para>
/// Classification is by exception TYPE, never by message text. That is the whole point: matching on prose is
/// what #231 exists to remove, and a message is free to be reworded or translated without breaking a consumer.
/// </para>
/// </summary>
public static class RagFailureCode
{
    /// <summary>Nothing was listening: connection refused, DNS failure. <i>Remedy: start Ollama / fix the URL.</i></summary>
    public const string BackendUnreachable = "backend_unreachable";

    /// <summary>
    /// The backend answered <c>200 OK</c> with a payload we cannot use — no answer field, wrong schema, empty
    /// text. <i>Remedy: fix the model/config; retrying gets the same answer (#222).</i>
    /// </summary>
    public const string BackendUnusableResponse = "backend_unusable_response";

    /// <summary>
    /// The backend accepted the request and then went silent — a stream with no further token inside its idle
    /// window (#230), or a body that never arrived (#266). <i>Remedy: the backend is wedged; restart it, or
    /// raise the timeout if the model is merely slow.</i>
    /// </summary>
    public const string BackendStalled = "backend_stalled";

    /// <summary>The request outlived <c>SemanticAnalyzer:TimeoutSeconds</c>. <i>Remedy: raise it, or use a smaller model.</i></summary>
    public const string BackendTimeout = "backend_timeout";

    /// <summary>
    /// The backend broke while serving us: a failure status (4xx/5xx), or the connection dying part-way through
    /// the answer. <i>Remedy: read the backend's own logs.</i> Both are the same instruction to the operator —
    /// something is listening and it is unwell — which is why they share a code rather than splitting hairs the
    /// reader cannot act on differently.
    /// </summary>
    public const string BackendError = "backend_error";

    /// <summary>The graph store failed while building the context. <i>Remedy: check the database.</i></summary>
    public const string StorageFailure = "storage_failure";

    /// <summary>Anything unrecognised — deliberately distinct from the classified ones, so it stays visible.</summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// Maps an exception to its failure class. Type-based by construction: a stall is
    /// <see cref="OllamaStalledException"/> rather than a reworded <see cref="OllamaResponseException"/>
    /// precisely so this method never has to read a message.
    /// </summary>
    public static string Classify(Exception ex)
    {
        return ex switch
        {
            OllamaStalledException => BackendStalled,
            OllamaResponseException => BackendUnusableResponse,
            SqliteException => StorageFailure,
            // A timeout and a caller-cancel both arrive as TaskCanceledException; callers that cancel are
            // handled before this is ever reached (the endpoint checks RequestAborted first, #227).
            TaskCanceledException or TimeoutException => BackendTimeout,
            // Reuse the retry classifier's judgement rather than re-deriving "is this a connect failure?" — it
            // already owns that call, including the platform table for HttpRequestError (#218/#221).
            HttpRequestException http => OllamaRetry.IsConnectError(http) ? BackendUnreachable : BackendError,
            // A backend that dies MID-ANSWER surfaces here, not on the arm above: the send already returned
            // 200 OK, so the failure lands on the body read and arrives as an IOException (HttpIOException in
            // .NET 8+), which is not an HttpRequestException. Without this arm the single most ordinary way for a
            // stream to break — the model process is killed while generating — classified as "unknown".
            IOException => BackendError,
            _ => Unknown
        };
    }
}
