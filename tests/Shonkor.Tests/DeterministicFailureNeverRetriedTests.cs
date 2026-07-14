// Licensed to Shonkor under the MIT License.

using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shonkor.Core.Models;
using Shonkor.Infrastructure.Services;

namespace Shonkor.Tests;

/// <summary>
/// A backend that answers <c>200 OK</c> with an unusable payload is hit <b>exactly once</b> (#222).
///
/// <para>
/// Both Ollama services carry a comment asserting this as a safety property:
/// </para>
/// <blockquote>
/// The <c>OllamaResponseException</c> below is raised AFTER a successful 200, so the retry pipeline has already
/// returned and cannot retry it. Deterministic-failure-is-never-retried is now <b>structural</b>.
/// </blockquote>
/// <para>
/// The property is worth having: a backend that answers <c>200</c> with garbage will answer identically on
/// every attempt, so retrying is pure waste against something that cannot succeed. #215 and #218 both began as
/// exactly this — a correct-sounding claim, argued in prose, that nothing was checking — and both turned out to
/// be broken once someone looked. So this one was checked.
/// </para>
/// <para>
/// <b>It holds — and the comment understates it.</b> Mutating each mechanism in turn:
/// </para>
/// <list type="table">
///   <item><term>validation moved <i>inside</i> the pipeline</term><description>still one request — the
///   <b>classifier</b> refuses it, because an unusable payload is not transient</description></item>
///   <item><term><c>IsTransient</c> made to call it transient</term><description>still one request —
///   <b>placement</b> saves it, exactly as the comment says</description></item>
///   <item><term><b>both</b></term><description><b>three requests. Fails.</b></description></item>
/// </list>
/// <para>
/// So it is not "structural" in the sense of resting on one mechanism: there are <b>two independent</b> ones,
/// and either alone suffices. These tests therefore assert the <b>outcome</b> — exactly one request reaches the
/// backend — rather than either mechanism. That is deliberate: a guard tied to placement would fail on a
/// harmless rearrangement and pass if the classifier silently changed. This one fails when, and only when, the
/// property genuinely breaks.
/// </para>
/// </summary>
public class DeterministicFailureNeverRetriedTests
{
    private static OllamaEmbeddingService Embedding(string url) =>
        OllamaClientFactory.CreateEmbeddingService(Config("EmbeddingService", url),
            NullLogger<OllamaEmbeddingService>.Instance);

    private static OllamaSemanticAnalyzer Analyzer(string url) =>
        OllamaClientFactory.CreateSemanticAnalyzer(Config("SemanticAnalyzer", url),
            NullLogger<OllamaSemanticAnalyzer>.Instance);

    private static IConfiguration Config(string section, string url) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{section}:OllamaUrl"] = url,
            [$"{section}:TimeoutSeconds"] = "10"
        }).Build();

    private static GraphNode Node() => new()
    {
        Id = "A", Name = "A", Type = "Class", Content = "class A {}", FilePath = "src/A.cs"
    };

    // ---- 200 OK with an unusable payload --------------------------------------------------------------

    [Fact]
    public async Task AnEmptyEmbedding_IsNotRetried_TheBackendWillReturnItAgain()
    {
        // 200 OK, valid JSON, no "embedding" field. The model is misconfigured; another attempt gets the same.
        using var backend = FakeOllamaBackend.ThatAnswers("{}");
        var embedding = Embedding(backend.Url);

        await Assert.ThrowsAsync<OllamaResponseException>(() => embedding.GenerateEmbeddingAsync("hello"));

        Assert.Equal(1, backend.Requests);
    }

    [Fact]
    public async Task MalformedJson_IsNotRetried_ForTheSameReason()
    {
        using var backend = FakeOllamaBackend.ThatAnswers("{ this is not json");
        var embedding = Embedding(backend.Url);

        await Assert.ThrowsAnyAsync<Exception>(() => embedding.GenerateEmbeddingAsync("hello"));

        Assert.Equal(1, backend.Requests);
    }

    [Fact]
    public async Task AnEmptyAnalysisResponse_IsNotRetried()
    {
        using var backend = FakeOllamaBackend.ThatAnswers("{}");
        var analyzer = Analyzer(backend.Url);

        await Assert.ThrowsAsync<OllamaResponseException>(() => analyzer.AnalyzeNodeAsync(Node()));

        Assert.Equal(1, backend.Requests);
    }

    // ---- 4xx: the other deterministic failure ----------------------------------------------------------

    [Fact]
    public async Task A4xx_IsNotRetried_ItWillFailIdenticallyEveryTime()
    {
        // Unlike the payload cases, this one IS decided by the retry classifier rather than by where the throw
        // sits — so it guards the other half of "deterministic failures are never retried".
        using var backend = new FakeOllamaBackend(HttpStatusCode.BadRequest);
        var embedding = Embedding(backend.Url);

        await Assert.ThrowsAnyAsync<Exception>(() => embedding.GenerateEmbeddingAsync("hello"));

        Assert.Equal(1, backend.Requests);
    }

    // ---- the contrast: a TRANSIENT failure on the same service IS retried -------------------------------

    [Fact]
    public async Task A503_OnTheSameService_IsRetried_SoTheseTestsAreMeasuringSomethingReal()
    {
        // Without this, all the assertions above would still pass if the retry pipeline were simply broken and
        // nothing was ever retried. This proves the pipeline is live on this exact call path.
        using var backend = new FakeOllamaBackend(HttpStatusCode.ServiceUnavailable);
        var embedding = Embedding(backend.Url);

        await Assert.ThrowsAnyAsync<Exception>(() => embedding.GenerateEmbeddingAsync("hello"));

        Assert.Equal(OllamaResilience.BackgroundAttempts, backend.Requests);
    }
}
