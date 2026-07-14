// Licensed to Shonkor under the MIT License.

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shonkor.Core.Interfaces;
using Polly;
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

    // ---- #116: the retry MECHANISM is Polly's now; assert it against the real pipeline ---------------
    //
    // The old test asserted on OllamaRetry.Backoff(int) — a hand-rolled exponential-with-jitter helper.
    // That helper is gone: computing backoff is precisely what Polly exists to do. Preserving it so a unit
    // test could keep asserting on it would have been dead code kept alive for the benefit of its own test.
    //
    // These assert the same properties, but end-to-end through OllamaResilience — a stronger check, because
    // it exercises the pipeline the services actually run on rather than a helper they no longer call.

    /// <summary>Fails a fixed number of times, then succeeds. Counts how many attempts the pipeline made.</summary>
    private sealed class FlakyHandler(int failures, Func<Exception>? error = null) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            if (Attempts <= failures) throw error?.Invoke() ?? new HttpRequestException("boom", null, HttpStatusCode.ServiceUnavailable);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static async Task<(int Attempts, Exception? Error)> RunAsync(
        ResiliencePipeline<HttpResponseMessage> pipeline, FlakyHandler handler)
    {
        using var client = new HttpClient(handler);
        try
        {
            await pipeline.ExecuteAsync(async ct =>
                await client.GetAsync("http://localhost/x", ct).ConfigureAwait(false), CancellationToken.None);
            return (handler.Attempts, null);
        }
        catch (Exception ex)
        {
            return (handler.Attempts, ex);
        }
    }

    [Fact]
    public async Task Background_RetriesATransientFailure_ThenSucceeds()
    {
        var handler = new FlakyHandler(failures: 2);
        var (attempts, error) = await RunAsync(OllamaResilience.Background, handler);

        Assert.Null(error);
        Assert.Equal(3, attempts); // the original + 2 retries
    }

    [Fact]
    public async Task Background_DoesNotRetryADeterministicFailure()
    {
        // A 400 will fail identically forever. Retrying it just wastes the backend's time and ours.
        var handler = new FlakyHandler(failures: 99, () => new HttpRequestException("bad", null, HttpStatusCode.BadRequest));
        var (attempts, error) = await RunAsync(OllamaResilience.Background, handler);

        Assert.NotNull(error);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Blocking_RetriesAConnectFailure_ExactlyOnce()
    {
        // The backend simply wasn't listening yet — cheap to retry, likely to fix itself.
        var handler = new FlakyHandler(failures: 1, () => new HttpRequestException("refused", new SocketException()));
        var (attempts, error) = await RunAsync(OllamaResilience.Blocking, handler);

        Assert.Null(error);
        Assert.Equal(2, attempts); // the original + exactly one retry
    }

    [Fact]
    public async Task Blocking_NeverRetriesATimeout_BecauseThatWouldDoubleAMinutesLongWait()
    {
        // THE critical property of the blocking RAG path. A generation can legitimately run for minutes;
        // retrying a timeout would make a human wait twice as long for an answer that is not coming.
        // A timeout is "transient" by every ordinary definition — and must still not be retried here.
        var handler = new FlakyHandler(failures: 99, () => new TaskCanceledException("timeout"));
        var (attempts, error) = await RunAsync(OllamaResilience.Blocking, handler);

        Assert.NotNull(error);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Background_RetriesATimeout_UnlikeTheBlockingPath()
    {
        // The counterpart: in background work nobody is waiting, so a timeout IS worth retrying. The two
        // policies genuinely differ — which is why they cannot both live on the shared HttpClient's handler.
        var handler = new FlakyHandler(failures: 1, () => new TaskCanceledException("timeout"));
        var (attempts, error) = await RunAsync(OllamaResilience.Background, handler);

        Assert.Null(error);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task CallerCancellation_IsNeverRetried()
    {
        // Previously enforced by a rule (inspect the token to tell caller-cancellation from a client
        // timeout). Now true by construction: a cancelled token aborts the pipeline itself.
        var handler = new FlakyHandler(failures: 99);
        using var client = new HttpClient(handler);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OllamaResilience.Background.ExecuteAsync(async ct =>
                await client.GetAsync("http://localhost/x", ct).ConfigureAwait(false), cts.Token).AsTask());

        Assert.Equal(0, handler.Attempts);
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
