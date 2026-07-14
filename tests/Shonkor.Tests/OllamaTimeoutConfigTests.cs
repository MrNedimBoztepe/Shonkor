// Licensed to Shonkor under the MIT License.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shonkor.Core.Models;
using Shonkor.Infrastructure.Services;

namespace Shonkor.Tests;

/// <summary>
/// The Ollama request timeout is configurable, and no longer silently overwrites the caller's (#215).
///
/// <para>
/// Both services used to assign <c>_httpClient.Timeout</c> unconditionally in their constructor — 1 minute for
/// embeddings, 2 for generation. Two consequences, neither intended:
/// </para>
/// <list type="bullet">
///   <item><b>An operator could not tune it.</b> A large model on slow hardware may legitimately need longer
///   than two minutes; someone who wants RAG to fail fast cannot ask for thirty seconds. Both required a
///   rebuild.</item>
///   <item><b>Configuring it the idiomatic way did nothing.</b> <c>AddHttpClient(c =&gt; c.Timeout = …)</c> is
///   exactly where you would set a timeout, and the constructor threw it away.</item>
/// </list>
/// <para>
/// It also made the real timeout path <b>untestable</b>: #213 had to assert "the blocking path never retries a
/// timeout" against the pipeline, because driving a genuine socket timeout would have taken two minutes. With a
/// configurable timeout that test can now be written for real, and it is — at the bottom of this file.
/// </para>
/// </summary>
public class OllamaTimeoutConfigTests
{
    private static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build();

    // ---- precedence ----------------------------------------------------------------------------------

    [Fact]
    public void WithNoConfiguration_TheDefaultsAreExactlyWhatWasHardCodedBefore()
    {
        // The whole change must be invisible to anyone who configures nothing.
        var embedClient = new HttpClient();
        _ = new OllamaEmbeddingService(embedClient, Config(), NullLogger<OllamaEmbeddingService>.Instance);
        Assert.Equal(TimeSpan.FromMinutes(1), embedClient.Timeout);

        var ragClient = new HttpClient();
        _ = new OllamaSemanticAnalyzer(ragClient, Config(), NullLogger<OllamaSemanticAnalyzer>.Instance);
        Assert.Equal(TimeSpan.FromMinutes(2), ragClient.Timeout);
    }

    [Fact]
    public void Configuration_SetsTheTimeout_SoAnOperatorCanTuneItWithoutARebuild()
    {
        var embedClient = new HttpClient();
        _ = new OllamaEmbeddingService(embedClient,
            Config(("EmbeddingService:TimeoutSeconds", "5")), NullLogger<OllamaEmbeddingService>.Instance);
        Assert.Equal(TimeSpan.FromSeconds(5), embedClient.Timeout);

        // A big model on slow hardware: give generation ten minutes rather than two.
        var ragClient = new HttpClient();
        _ = new OllamaSemanticAnalyzer(ragClient,
            Config(("SemanticAnalyzer:TimeoutSeconds", "600")), NullLogger<OllamaSemanticAnalyzer>.Instance);
        Assert.Equal(TimeSpan.FromMinutes(10), ragClient.Timeout);
    }

    [Fact]
    public void ATimeoutTheCallerAlreadySet_IsRespected_NotSilentlyOverwritten()
    {
        // This is what AddHttpClient(c => c.Timeout = …) does. It used to be discarded.
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(7) };

        _ = new OllamaSemanticAnalyzer(client, Config(), NullLogger<OllamaSemanticAnalyzer>.Instance);

        Assert.Equal(TimeSpan.FromSeconds(7), client.Timeout);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-30")]
    public void AnUnusableConfiguredValue_FallsBackToTheDefault_RatherThanBreakingTheClient(string value)
    {
        // A zero or negative HttpClient.Timeout throws, so a typo in config must not be able to take the
        // process down — it falls back to the shipped default.
        var client = new HttpClient();

        _ = new OllamaSemanticAnalyzer(client,
            Config(("SemanticAnalyzer:TimeoutSeconds", value)), NullLogger<OllamaSemanticAnalyzer>.Instance);

        Assert.Equal(TimeSpan.FromMinutes(2), client.Timeout);
    }

    // ---- the end-to-end guard #213 could not write ----------------------------------------------------

    /// <summary>
    /// A real socket timeout, driven for real: a backend that accepts the connection and then never answers,
    /// against a 2-second configured timeout.
    /// <para>
    /// This is the rule <see cref="OllamaResilience.Blocking"/> exists for — a timeout is "transient" in every
    /// ordinary sense, but retrying a RAG generation that already ran to timeout only doubles a long wait for a
    /// human. #213 could only assert it against the pipeline, with a synthesised exception. Now the actual
    /// <c>HttpClient</c> raises the actual timeout and the count still has to be one.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheRagPath_AgainstABackendThatNeverAnswers_TimesOutAfterExactlyOneAttempt()
    {
        using var backend = FakeOllamaBackend.ThatNeverResponds();
        var analyzer = new OllamaSemanticAnalyzer(
            new HttpClient(),
            Config(("SemanticAnalyzer:OllamaUrl", backend.Url), ("SemanticAnalyzer:TimeoutSeconds", "2")),
            NullLogger<OllamaSemanticAnalyzer>.Instance);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            analyzer.GenerateRAGResponseAsync("what does the graph do?", [Node("A")]));

        Assert.Equal(1, backend.Requests);
    }

    /// <summary>
    /// The mirror image, on the same service and the same kind of client: background work DOES retry a timeout,
    /// so the asymmetry that justifies the call-site policy placement (#176/#179/#213) survives a real timeout
    /// and not just a synthesised one.
    /// </summary>
    [Fact]
    public async Task TheEnrichmentPath_AgainstTheSameBackend_RetriesTheTimeout()
    {
        using var backend = FakeOllamaBackend.ThatNeverResponds();
        var analyzer = new OllamaSemanticAnalyzer(
            new HttpClient(),
            Config(("SemanticAnalyzer:OllamaUrl", backend.Url), ("SemanticAnalyzer:TimeoutSeconds", "2")),
            NullLogger<OllamaSemanticAnalyzer>.Instance);

        // The retries are exhausted and the timeout is rethrown — the throw is not the point, the count is.
        await Assert.ThrowsAnyAsync<Exception>(() => analyzer.AnalyzeNodeAsync(Node("A")));

        Assert.Equal(OllamaResilience.BackgroundAttempts, backend.Requests);
    }

    private static GraphNode Node(string id) => new()
    {
        Id = id, Name = id, Type = "Class", Content = $"class {id} {{}}", FilePath = $"src/{id}.cs"
    };
}
