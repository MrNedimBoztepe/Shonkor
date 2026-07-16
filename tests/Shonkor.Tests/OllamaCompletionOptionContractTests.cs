// Licensed to Shonkor under the MIT License.

namespace Shonkor.Tests;

/// <summary>
/// Each Ollama call site keeps the <c>HttpCompletionOption</c> its guarantee rests on (#244).
///
/// <para>
/// Two resilience guarantees are decided by the completion option, not by the retry policy: the option is what
/// puts a body-phase failure <i>inside</i> the pipeline (retryable) or after it has already returned (not).
/// Flip the blocking send to a full body read and #221's bug returns — invisibly on Windows, per the platform
/// table in <c>OllamaRetry</c>. Flip the streaming send and a stream becomes retryable mid-answer, so a token
/// could be emitted twice. Flip a background send the other way and it silently stops retrying the failures it
/// exists to absorb.
/// </para>
/// <para>
/// The behavioural guards (<c>StreamingNoRetryTests</c>, <c>DeterministicFailureNeverRetriedTests</c>,
/// <c>OllamaBlockingPolicyTests</c>) already fail when someone breaks this — but only because their fixtures
/// happen to provoke a body-phase failure, and none of them tells a reader the invariant exists <i>before</i>
/// they break it. This is the direct guard #244 asked for: it fails on the change itself, and its message says
/// where the contract is written down.
/// </para>
/// <para>
/// It reads source, so it is deliberately narrow: comment lines are stripped first (the sites now <i>discuss</i>
/// the other option in prose, which must not count), and each method is sliced out so an option in one method
/// cannot satisfy the assertion for another.
/// </para>
/// </summary>
public class OllamaCompletionOptionContractTests
{
    private const string Contract =
        "The completion-option contract is stated in OllamaResilience's class doc (#244) — read it before changing this.";

    private static string Analyzer() => CodeOnly(File.ReadAllText(
        RepoPaths.File("src", "Shonkor.Infrastructure", "Services", "OllamaSemanticAnalyzer.cs")));

    private static string Embedding() => CodeOnly(File.ReadAllText(
        RepoPaths.File("src", "Shonkor.Infrastructure", "Services", "OllamaEmbeddingService.cs")));

    /// <summary>Drops <c>//</c> and <c>///</c> lines so the assertions are about CODE, not the prose about it.</summary>
    private static string CodeOnly(string source) => string.Join('\n',
        source.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    /// <summary>The body of one method — so an option used in a sibling method cannot answer for this one.</summary>
    private static string Method(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find `{signature}`. If it was renamed, update this contract test — do not delete it. {Contract}");

        var from = start + signature.Length;
        var end = new[]
        {
            source.IndexOf("\n    public ", from, StringComparison.Ordinal),
            source.IndexOf("\n    private ", from, StringComparison.Ordinal),
            source.IndexOf("\n    internal ", from, StringComparison.Ordinal)
        }.Where(i => i >= 0).DefaultIfEmpty(source.Length).Min();

        return source[start..end];
    }

    [Fact]
    public void TheBlockingRagSend_ReadsHeadersOnly_SoAGenerationIsNeverSilentlyReRun()
    {
        var body = Method(Analyzer(),
            "public async Task<string> GenerateRAGResponseAsync(string query, IReadOnlyList<GraphNode> contextNodes, RagPromptOptions options");

        Assert.Contains("HttpCompletionOption.ResponseHeadersRead", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStreamingSend_ReadsHeadersOnly_SoATokenCanNeverBeEmittedTwice()
    {
        var body = Method(Analyzer(), "public async IAsyncEnumerable<string> StreamRAGResponseAsync(");

        Assert.Contains("HttpCompletionOption.ResponseHeadersRead", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBackgroundAnalyzeSend_ReadsTheFullBodyInsideThePipeline_SoAMidBodyFailureIsRetried()
    {
        var body = Method(Analyzer(),
            "public async Task<SemanticAnalysisResult> AnalyzeNodeAsync(GraphNode node, CancellationToken cancellationToken = default)");

        Assert.Contains("PostAsJsonAsync", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ResponseHeadersRead", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBackgroundEmbeddingSend_ReadsTheFullBodyInsideThePipeline_SoAMidBodyFailureIsRetried()
    {
        var body = Method(Embedding(),
            "public async Task<float[]> GenerateEmbeddingAsync(string text, EmbeddingKind kind, CancellationToken cancellationToken = default)");

        Assert.Contains("PostAsJsonAsync", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ResponseHeadersRead", body, StringComparison.Ordinal);
    }
}
