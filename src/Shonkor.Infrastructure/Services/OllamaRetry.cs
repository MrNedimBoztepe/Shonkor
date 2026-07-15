// Licensed to Shonkor under the MIT License.

using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Shonkor.Infrastructure.Services;

/// <summary>
/// Ollama accepted the request and answered, but the payload was unusable (an empty embedding, an empty
/// completion, unparseable JSON). This is a DETERMINISTIC failure: the same request will fail the same way,
/// so it must never be retried, and it does not mean the backend is unavailable. Replaces the former bare
/// <c>throw new Exception(...)</c>, which no classifier could tell apart from a transport failure.
/// </summary>
public sealed class OllamaResponseException : Exception
{
    public OllamaResponseException(string message) : base(message) { }
}

/// <summary>
/// Retry <b>classification</b> for the Ollama-backed services: retry only what a retry can plausibly fix
/// (transport failures, 5xx, 408/429, HttpClient timeouts) and never what it cannot (a deterministic 4xx, an
/// unusable payload) — and never a cancellation the CALLER asked for.
///
/// <para>
/// Since #116 this class is <b>only</b> the predicate. The <i>mechanism</i> — when to wait, how long, how
/// many times — belongs to Polly (<see cref="OllamaResilience"/>), which is the canonical implementation of
/// exactly that and was the only thing being hand-rolled. What is <i>not</i> canonical, and stays here, is
/// the domain judgement: <b>what counts as transient for this particular backend.</b> A library cannot know
/// that Ollama answers 200 with an empty embedding when a model is still loading.
/// </para>
///
/// <para>
/// Keeping the predicate separate also keeps it <b>testable without a network</b> (<c>OllamaRetryTests</c>),
/// which was the original reason for hand-rolling and remains a good one.
/// </para>
/// </summary>
public static class OllamaRetry
{
    /// <summary>
    /// Whether a failed attempt is worth repeating. A <see cref="TaskCanceledException"/> only reaches here
    /// when the caller's token was NOT triggered (the call sites rethrow caller-cancellation first), so it
    /// is an HttpClient timeout — transient.
    /// </summary>
    public static bool IsTransient(Exception ex) => ex switch
    {
        HttpRequestException http => IsTransientStatus(http.StatusCode),
        TaskCanceledException => true,   // HttpClient timeout (caller-cancellation is filtered out first)
        SocketException => true,
        _ => false                       // OllamaResponseException, JsonException, 4xx, … : deterministic
    };

    /// <summary>
    /// A missing status means the response never arrived (connect/DNS failure) — retryable. Public since #116:
    /// Polly's <c>ShouldHandle</c> inspects the <see cref="HttpResponseMessage"/> <i>before</i> the service
    /// turns a bad status into an exception, so the predicate needs the status-code path too.
    /// </summary>
    public static bool IsTransientStatus(HttpStatusCode? status) => status switch
    {
        null => true,
        HttpStatusCode.RequestTimeout => true,
        HttpStatusCode.TooManyRequests => true,
        var s => (int)s >= 500
    };

    /// <summary>
    /// A connection-level failure: the request never reached a responding server. Used by the BLOCKING RAG
    /// path, which must not retry a slow/timed-out generation (that would double an already minutes-long
    /// wait) but may cheaply retry a backend that was simply not listening yet.
    ///
    /// <para>
    /// <b>A timeout is not a connection failure, even though the socket layer reports one on the way out
    /// (#215).</b> When <see cref="HttpClient"/>'s timeout elapses it tears the connection down, so the real
    /// exception chain is:
    /// </para>
    /// <code>
    /// TaskCanceledException → TimeoutException → TaskCanceledException → IOException → SocketException
    /// </code>
    /// <para>
    /// This method used to scan that chain for <i>any</i> <see cref="SocketException"/> — and so answered
    /// <c>true</c> for a timeout. The blocking pipeline therefore retried the one failure it exists to never
    /// retry, doubling a minutes-long wait for a human. It was invisible because the unit test that "covered"
    /// the rule constructed a bare <c>TaskCanceledException</c>, which is not the shape <c>HttpClient</c>
    /// actually throws; only driving a real socket timeout end-to-end exposed it.
    /// </para>
    /// <para>
    /// So a cancellation or timeout anywhere in the chain settles it first: whatever the socket said on the way
    /// down, we <i>did</i> reach the backend — it just did not answer in time. Only a failure with no
    /// cancellation in it is a genuine "never got there".
    /// </para>
    /// </summary>
    /// <remarks>
    /// <b>What this predicate can and cannot know (#218, then #221).</b>
    /// <para>
    /// #218 found that a <c>200 OK</c> whose body dies half-way — the backend answered, the model was already
    /// generating, the connection dropped — surfaces as
    /// <c>HttpRequestException → IOException → SocketException</c>: the same wreckage a refused connection
    /// leaves. It tried to separate the two with <see cref="HttpRequestException.HttpRequestError"/>, which on
    /// Windows reports <c>ConnectionError</c> for a refusal and <c>Unknown</c> for a mid-body death.
    /// </para>
    /// <para>
    /// <b>That was measured on one platform, and it does not hold on the other.</b> On Ubuntu 24.04 a mid-body
    /// death <i>also</i> reports <c>ConnectionError</c> — indistinguishable from a refusal:
    /// </para>
    /// <code>
    /// failure                  Windows           Linux
    /// connection refused       ConnectionError   ConnectionError
    /// reset before response    Unknown           ConnectionError
    /// 200 then dies mid-body   Unknown           ConnectionError   &lt;-- same as a refusal
    /// </code>
    /// <para>
    /// So the exception <b>cannot</b> tell us whether a response arrived, on any portable basis — and #218's fix
    /// therefore did nothing at all on the platform this actually ships on. The line is now drawn where it is
    /// genuinely knowable: the blocking RAG path reads only the <b>headers</b> inside its retry pipeline
    /// (<c>HttpCompletionOption.ResponseHeadersRead</c>), so a body that dies mid-answer fails <i>after</i> the
    /// pipeline has returned and cannot be retried <b>by construction</b> (#221) — the same mechanism that
    /// already makes an unusable payload unretriable (#222).
    /// </para>
    /// <para>
    /// This predicate is consequently only ever asked about failures that happened <b>before a response
    /// arrived</b>, and for those the values below agree across platforms. It classifies pre-response transport
    /// failures; it is not, as #218 believed, an oracle for "did we reach the backend".
    /// </para>
    /// </remarks>
    public static bool IsConnectError(Exception ex)
    {
        if (IsTimeoutOrCancellation(ex)) return false;

        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException http)
            {
                return NeverReachedTheBackend(http.HttpRequestError);
            }

            // A bare SocketException, with no HttpRequestException wrapping it and no cancellation in play,
            // means the transport failed before HTTP got involved at all.
            if (current is SocketException) return true;
        }
        return false;
    }

    /// <summary>
    /// The <see cref="HttpRequestError"/> values that mean the request <b>never reached a responding server</b>
    /// — the only class of failure the blocking RAG path may cheaply retry. Everything else (a response that
    /// arrived and then went wrong, or an unclassified transport error) means the backend was engaged, and
    /// re-running the generation would double a wait the caller is already enduring.
    /// </summary>
    private static bool NeverReachedTheBackend(HttpRequestError error) => error switch
    {
        HttpRequestError.ConnectionError => true,      // refused, unreachable, reset while connecting
        HttpRequestError.NameResolutionError => true,  // DNS did not resolve
        HttpRequestError.SecureConnectionError => true,// TLS handshake failed
        HttpRequestError.ProxyTunnelError => true,     // never got through the proxy
        _ => false                                     // Unknown, ResponseEnded, InvalidResponse, …
    };

    /// <summary>
    /// Whether the failure is a timeout or a cancellation — i.e. the attempt was <i>abandoned</i> rather than
    /// having failed to reach the backend. <see cref="TaskCanceledException"/> derives from
    /// <see cref="OperationCanceledException"/>, so both an <c>HttpClient</c> timeout and a caller-triggered
    /// cancellation are caught here.
    /// </summary>
    private static bool IsTimeoutOrCancellation(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is OperationCanceledException or TimeoutException) return true;
        }
        return false;
    }

    // Backoff(int) lived here until #116. It computed exponential delay with jitter by hand — precisely the
    // mechanism Polly exists to provide, and now does (OllamaResilience: DelayBackoffType.Exponential,
    // UseJitter). Keeping a hand-rolled copy alive so an old unit test could assert on it would have been
    // dead code preserved for the benefit of its own test; the behaviour is now asserted against the real
    // pipeline instead, which is a stronger check than the one it replaces.
}
