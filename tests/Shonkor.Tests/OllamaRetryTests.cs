// Licensed to Shonkor under the MIT License.

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shonkor.Core.Interfaces;
using Shonkor.Infrastructure.Services;

namespace Shonkor.Tests;

/// <summary>
/// TICKET-210 retry hygiene: retry only what a retry can fix (transport, 5xx, 408/429, client timeout),
/// never a deterministic failure (4xx, an unusable payload), and never a cancellation the caller triggered.
/// </summary>
public class OllamaRetryTests
{
    private static HttpRequestException Http(HttpStatusCode? status, Exception? inner = null) =>
        new("boom", inner, status);

    [Fact]
    public void Transient_TransportAndServerFailures_AreRetryable()
    {
        Assert.True(OllamaRetry.IsTransient(Http(null)));                                  // never reached a server
        Assert.True(OllamaRetry.IsTransient(Http(HttpStatusCode.InternalServerError)));    // 500
        Assert.True(OllamaRetry.IsTransient(Http(HttpStatusCode.ServiceUnavailable)));     // 503
        Assert.True(OllamaRetry.IsTransient(Http(HttpStatusCode.RequestTimeout)));         // 408
        Assert.True(OllamaRetry.IsTransient(Http(HttpStatusCode.TooManyRequests)));        // 429
        Assert.True(OllamaRetry.IsTransient(new TaskCanceledException()));                 // HttpClient timeout
        Assert.True(OllamaRetry.IsTransient(new SocketException()));
    }

    [Fact]
    public void Transient_DeterministicFailures_AreNotRetryable()
    {
        // A 4xx will fail identically on every attempt — retrying only wastes the caller's time.
        Assert.False(OllamaRetry.IsTransient(Http(HttpStatusCode.BadRequest)));
        Assert.False(OllamaRetry.IsTransient(Http(HttpStatusCode.NotFound)));
        Assert.False(OllamaRetry.IsTransient(Http(HttpStatusCode.UnprocessableEntity)));
        // The backend answered; the payload was unusable. Same request → same answer.
        Assert.False(OllamaRetry.IsTransient(new OllamaResponseException("empty embedding")));
        Assert.False(OllamaRetry.IsTransient(new InvalidOperationException()));
    }

    [Fact]
    public void ConnectError_DistinguishesUnreachableFromMidFlightFailure()
    {
        // The blocking RAG path retries ONLY these: the request never reached a responding backend.
        Assert.True(OllamaRetry.IsConnectError(Http(null)));
        Assert.True(OllamaRetry.IsConnectError(Http(null, new SocketException())));
        Assert.True(OllamaRetry.IsConnectError(new HttpRequestException("dns", new SocketException())));

        // These reached the server (or timed out mid-generation) — retrying doubles a minutes-long wait.
        Assert.False(OllamaRetry.IsConnectError(Http(HttpStatusCode.InternalServerError)));
        Assert.False(OllamaRetry.IsConnectError(new TaskCanceledException()));
        Assert.False(OllamaRetry.IsConnectError(new OllamaResponseException("empty")));
    }

    [Fact]
    public void Backoff_GrowsExponentially_AndCarriesJitter()
    {
        Assert.True(OllamaRetry.Backoff(1) < OllamaRetry.Backoff(3));
        Assert.True(OllamaRetry.Backoff(1) >= TimeSpan.FromSeconds(1));
        // Jitter: repeated draws for the same attempt should not all be identical.
        var draws = Enumerable.Range(0, 20).Select(_ => OllamaRetry.Backoff(1)).Distinct().Count();
        Assert.True(draws > 1, "backoff must carry jitter so workers don't retry in lockstep");
    }

    // ---- Behaviour: a triggered cancellation is never retried ----------------------------------------

    /// <summary>Counts attempts and always fails with a transient error, so a retry loop would spin.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            throw new HttpRequestException("backend down", null, HttpStatusCode.ServiceUnavailable);
        }
    }

    private static OllamaEmbeddingService NewService(HttpMessageHandler handler)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        return new OllamaEmbeddingService(new HttpClient(handler), config, NullLogger<OllamaEmbeddingService>.Instance);
    }

    [Fact]
    public async Task Embedding_CallerCancellation_IsNotRetried()
    {
        var handler = new CountingHandler();
        var service = NewService(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GenerateEmbeddingAsync("text", EmbeddingKind.Document, cts.Token));

        // An already-cancelled token must not consume retry attempts (previously: caught, delayed, retried).
        Assert.True(handler.Calls <= 1, $"a cancelled call must not be retried, saw {handler.Calls} attempts");
    }

    [Fact]
    public async Task Embedding_DeterministicClientError_IsNotRetried()
    {
        var handler = new BadRequestHandler();
        var service = NewService(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => service.GenerateEmbeddingAsync("text", EmbeddingKind.Document, CancellationToken.None));

        Assert.Equal(1, handler.Calls); // a 400 is retried zero times
    }

    private sealed class BadRequestHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
        }
    }
}
