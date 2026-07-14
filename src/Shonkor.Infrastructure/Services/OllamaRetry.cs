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
    /// </summary>
    public static bool IsConnectError(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is SocketException) return true;
            if (current is HttpRequestException { StatusCode: null }) return true;
        }
        return false;
    }

    // Backoff(int) lived here until #116. It computed exponential delay with jitter by hand — precisely the
    // mechanism Polly exists to provide, and now does (OllamaResilience: DelayBackoffType.Exponential,
    // UseJitter). Keeping a hand-rolled copy alive so an old unit test could assert on it would have been
    // dead code preserved for the benefit of its own test; the behaviour is now asserted against the real
    // pipeline instead, which is a stronger check than the one it replaces.
}
