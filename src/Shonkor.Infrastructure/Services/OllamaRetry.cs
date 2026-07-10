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
/// Retry classification for the Ollama-backed services. The rule is: retry only what a retry can plausibly
/// fix (transport failures, 5xx, 408/429, HttpClient timeouts) and never what it cannot (a deterministic 4xx,
/// an unusable payload) — and never a cancellation the CALLER asked for.
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

    /// <summary>A missing status means the response never arrived (connect/DNS failure) — retryable.</summary>
    private static bool IsTransientStatus(HttpStatusCode? status) => status switch
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

    /// <summary>Exponential backoff with jitter, so concurrent workers don't retry in lockstep.</summary>
    public static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(Math.Pow(2, attempt - 1) * 1000 + Random.Shared.Next(0, 500));
}
