// Licensed to Shonkor under the MIT License.

using Microsoft.ML.Tokenizers;
using Shonkor.Core.Services;

namespace Shonkor.Tests;

/// <summary>
/// <c>TokenBudget.Estimate</c> against REAL tokenizers (#173).
///
/// <para>
/// The estimator gates when a Markdown section splits and how much of a body gets embedded, and its entire
/// safety argument was that it is <i>conservative</i> — it over-counts, so a section splits early rather than
/// being handed to the backend and silently truncated. That argument was never checked. It turned out to be
/// <b>false</b> for whole input classes: hex, base64 and GUIDs under-counted by 2–3×, because the
/// 4-chars-per-token rule assumes text compresses like English words, and out-of-vocabulary alphanumeric runs
/// shred to ~1.5 chars/token instead. A section of base64 passed a 1000-token budget check while really
/// costing ~2750 — over the window, silently truncated. Exactly the bug #111 removed, still live.
/// </para>
/// <para>
/// The oracle is the actual <c>bert-base-uncased</c> WordPiece vocab that <c>nomic-embed-text</c> uses (vendored
/// under <c>Fixtures/</c>), plus cl100k as an independent second tokenizer so the result does not hinge on one
/// vocabulary's quirks.
/// </para>
/// <para>
/// <b>Why the invariant is a bounded under-count and not "never under-counts".</b> A strict upper bound is not
/// achievable at a usable price: a sub-word tokenizer costs an out-of-vocabulary run ~1.5–2 chars/token
/// (<c>Rrf</c> → 2 tokens, <c>Ollama</c> → 3) while real words cost ~4, so charging every run at the worst
/// case would over-count English prose ~2.5× and split it into needlessly small sections — diluting retrieval,
/// a worse bug than the one being fixed. What the design actually relies on is that the under-count is bounded
/// by <see cref="TokenBudget.UnderCountSafetyFactor"/> and that the budgets are sized to absorb it. Both halves
/// are pinned below.
/// </para>
/// </summary>
public class TokenBudgetRealTokenizerTests
{
    private static readonly BertTokenizer Bert = BertTokenizer.Create(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "bert-base-uncased-vocab.txt"));

    private static readonly TiktokenTokenizer Cl100k = TiktokenTokenizer.CreateForModel("gpt-4");

    /// <summary>Content tokens only — the [CLS]/[SEP] wrappers are not part of what the body costs.</summary>
    private static int BertCount(string text) => Bert.EncodeToIds(text, addSpecialTokens: false).Count;

    private const string NaturalProse =
        "The retrieval graph links each symbol to the code that references it, so a query about one class " +
        "surfaces its callers, its interfaces, and the modules that depend on it, ranked by how closely they relate.";

    /// <summary>
    /// The input classes a real corpus contains. Each must be costed at or above what the real tokenizers
    /// charge — for these, the estimator is genuinely an upper bound.
    /// </summary>
    public static TheoryData<string, string> RealCorpus() => new()
    {
        { "english prose", NaturalProse },
        { "cjk", new string('文', 200) },
        { "cjk mixed with latin", "このクラスは api トークンを hash して保存します。" },
        { "fenced code", "```csharp\npublic static int Estimate(string? text) {\n    if (string.IsNullOrEmpty(text)) return 0;\n    var tokens = 0;\n}\n```" },
        { "markdown table", string.Concat(Enumerable.Repeat("| col_a | col_b | col_c |\n|---|---|---|\n", 6)) },
        // The classes that were silently under-counted before #173 — the whole point of the fix.
        { "hex digests", string.Join(" ", Enumerable.Repeat("deadbeefcafef00d1234567890abcdef", 8)) },
        { "base64 blob", "aGVsbG8gd29ybGQgdGhpcyBpcyBhIGxvbmcgYmFzZTY0IHN0cmluZyBwYXlsb2Fk" },
        { "guids", string.Join(" ", Enumerable.Repeat("a1b2c3d4-e5f6-7890-abcd-ef1234567890", 6)) },
        { "url with query", "https://huggingface.co/bert-base-uncased/resolve/main/vocab.txt?download=true&ref=xyz123" },
        { "sha256 digest", "e3b0c44298fc1c149afbf4c8996fb924" },
    };

    [Theory]
    [MemberData(nameof(RealCorpus))]
    public void Estimate_NeverUnderCounts_TheRealTokenizers(string label, string text)
    {
        var estimate = TokenBudget.Estimate(text);

        Assert.True(estimate >= BertCount(text),
            $"[{label}] estimated {estimate} tokens but bert-base-uncased (nomic-embed-text's tokenizer) charges " +
            $"{BertCount(text)}. Under-counting hands the backend a body it silently truncates — #111's bug.");
        Assert.True(estimate >= Cl100k.CountTokens(text),
            $"[{label}] estimated {estimate} tokens but cl100k charges {Cl100k.CountTokens(text)}.");
    }

    /// <summary>
    /// Whole real files — the unit the estimator is actually applied to. A file mixes prose, punctuation and
    /// identifiers, and on that mixture the estimate stays above both tokenizers with margin to spare.
    /// </summary>
    [Theory]
    [InlineData("src/Shonkor.Core/Services/TokenBudget.cs")]
    [InlineData("src/Shonkor.Infrastructure/Storage/SqliteGraphStorageProvider.cs")]
    [InlineData("README.md")]
    [InlineData("CHANGELOG.md")]
    public void Estimate_NeverUnderCounts_OnWholeRealFiles(string relativePath)
    {
        var text = File.ReadAllText(RepoPaths.File(relativePath));
        var estimate = TokenBudget.Estimate(text);

        Assert.True(estimate >= BertCount(text),
            $"{relativePath}: estimated {estimate} but bert charges {BertCount(text)}");
        Assert.True(estimate >= Cl100k.CountTokens(text),
            $"{relativePath}: estimated {estimate} but cl100k charges {Cl100k.CountTokens(text)}");
    }

    /// <summary>
    /// A bare out-of-vocabulary identifier IS under-counted — <c>Rrf</c> estimates 1 token and costs 2 — and
    /// that is accepted, not a bug: removing it would mean charging every word at the worst case and
    /// over-splitting prose. What must hold is that the under-count stays inside the factor the budgets are
    /// sized for. If a tokenizer ever shreds an identifier harder than this, the budget headroom is gone and
    /// the estimator has to change.
    /// </summary>
    [Theory]
    [InlineData("Rrf")]
    [InlineData("Ollama")]
    [InlineData("Reindex")]
    [InlineData("Hotspots")]
    [InlineData("SearchAsync")]
    [InlineData("IsUnitLength")]
    [InlineData("McpToolContext")]
    [InlineData("SqliteGraphStorageProvider")]
    [InlineData("NormalizeExistingEmbeddingsOnceAsync")]
    public void Estimate_OnOutOfVocabularyIdentifiers_UnderCountsOnlyWithinTheSafetyFactor(string identifier)
    {
        var estimate = TokenBudget.Estimate(identifier);
        var ceiling = estimate * TokenBudget.UnderCountSafetyFactor;

        Assert.True(BertCount(identifier) <= ceiling,
            $"'{identifier}': estimated {estimate}, real cost {BertCount(identifier)} — that exceeds the " +
            $"{TokenBudget.UnderCountSafetyFactor}x under-count the budgets are sized to absorb ({ceiling}).");
        Assert.True(Cl100k.CountTokens(identifier) <= ceiling,
            $"'{identifier}': estimated {estimate}, cl100k charges {Cl100k.CountTokens(identifier)} > {ceiling}.");
    }

    /// <summary>
    /// The other half of the safety argument, and the reason a bounded under-count is tolerable at all: the
    /// budgets leave enough headroom that even a worst-case under-count still fits the backend's window. If
    /// someone raises SectionBudgetTokens toward 2048 "because that's the real limit", this fails — which is
    /// the point, because that change would re-open the silent-truncation hole.
    /// </summary>
    [Fact]
    public void TheBudgets_AbsorbAFullFactorUnderCount_WithoutOverflowingTheModelWindow()
    {
        Assert.True(
            TokenBudget.SectionBudgetTokens * TokenBudget.UnderCountSafetyFactor <= TokenBudget.ModelWindowTokens,
            $"A section may be estimated at {TokenBudget.SectionBudgetTokens} tokens while really costing up to " +
            $"{TokenBudget.SectionBudgetTokens * TokenBudget.UnderCountSafetyFactor} — which must still fit the " +
            $"{TokenBudget.ModelWindowTokens}-token window, or the backend truncates it silently.");

        Assert.True(
            TokenBudget.EmbeddingBodyTokens * TokenBudget.UnderCountSafetyFactor <= TokenBudget.ModelWindowTokens,
            "the embedding body budget must leave the same headroom");
    }

    /// <summary>
    /// The counterweight: an estimator that over-counts wildly is not "safe", it just splits English prose into
    /// needlessly small sections and dilutes retrieval. Prose must stay close to the real cost.
    /// </summary>
    [Fact]
    public void Estimate_OnNaturalProse_IsNotAbsurdlyOverConservative()
    {
        var estimate = TokenBudget.Estimate(NaturalProse);
        var real = BertCount(NaturalProse);

        Assert.True(estimate <= real * 2,
            $"prose estimated at {estimate} tokens against a real cost of {real} — over-splitting English prose " +
            "dilutes retrieval, which is the failure mode on the other side of this trade-off.");
        Assert.True(estimate >= real); // ...while still never under-counting it
    }

    /// <summary>
    /// The specific regression that #173 found. Before the fix, base64 estimated 16 tokens against a real cost
    /// of 44 — a 2.75× under-count, well past what the budget headroom absorbs. Reverting the digit rule in
    /// <c>Estimate</c> puts this back and this test says so.
    /// </summary>
    [Fact]
    public void Base64_TheClassThatBreachedTheSafetyFactor_IsNowCostedAboveItsRealTokenCount()
    {
        const string base64 = "aGVsbG8gd29ybGQgdGhpcyBpcyBhIGxvbmcgYmFzZTY0IHN0cmluZyBwYXlsb2Fk";

        var estimate = TokenBudget.Estimate(base64);
        var real = BertCount(base64);

        Assert.True(estimate >= real,
            $"base64 estimated {estimate} against a real cost of {real}. The old 4-chars/token rule scored it " +
            "at 16 — so 1000 estimated tokens of base64 were really ~2750, over the window and truncated.");
    }
}
