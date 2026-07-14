// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Models;
using Shonkor.Core.Services;

namespace Shonkor.Tests;

/// <summary>
/// #111 (budget in tokens, not characters) and #112 (nest sections by heading level).
/// </summary>
public class TokenBudgetAndNestingTests
{
    private static async Task<(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)> ParseAsync(string md)
        => await new MarkdownHierarchyParser().ParseAsync("/docs/x.md", md);

    // ---- #111: the character proxy silently truncated non-prose --------------------------------------

    [Fact]
    public void Estimate_EnglishProse_IsAboutFourCharsPerToken()
    {
        // The assumption the old 4000-char rule rested on. It is fine — for this one case.
        var prose = string.Join(" ", Enumerable.Repeat("architecture", 100)); // 12 chars + space
        var tokens = TokenBudget.Estimate(prose);

        Assert.InRange(tokens, 250, 400); // ~1200 chars → ~300 tokens
    }

    [Fact]
    public void Estimate_Cjk_CostsRoughlyOneTokenPerCharacter()
    {
        // This is the bug. Under the old rule a 4000-character section "fits". In CJK that is ~4000 tokens —
        // roughly twice the backend's 2048-token window — so it was handed over and silently truncated.
        var cjk = new string('文', 4000);

        Assert.Equal(4000, TokenBudget.Estimate(cjk));
        Assert.True(cjk.Length <= 4000, "…and the old 4000-CHARACTER rule would have called this section a perfect fit");
        Assert.False(TokenBudget.Fits(cjk, TokenBudget.SectionBudgetTokens),
            "4000 CJK characters must not be treated as fitting the embedding window");
    }

    [Fact]
    public void Estimate_PunctuationDenseText_CostsMoreThanProseOfTheSameLength()
    {
        // Fenced code and tables: same character count, far more tokens. A character cap treats them as equal.
        // The prose sample is real words, not one 400-character run: since #173 a run that long is charged per
        // character (a digit-free blob — minified JS, base64 — is not English), so it would no longer stand in
        // for prose at all.
        var prose = string.Join(" ", Enumerable.Repeat("the graph links code", 20)); // 420 chars of real words
        var table = string.Concat(Enumerable.Repeat("|x|y|", 84));                   // 420 chars, punctuation-heavy

        Assert.True(TokenBudget.Estimate(table) > TokenBudget.Estimate(prose),
            "punctuation-dense text must cost more tokens than prose of the same character length");
    }

    [Fact]
    public async Task ProseSection_UnderTheOldCharRule_StillDoesNotSplit()
    {
        // The budget is calibrated so English prose lands exactly where it did before. If this breaks, the
        // doc-sections golden set regresses — the change must fix CJK without disturbing what already worked.
        var body = string.Join("\n\n", Enumerable.Repeat("Some ordinary architectural prose about the graph.", 40));
        var (nodes, _) = await ParseAsync($"# Chapter\n\n{body}\n");

        Assert.Single(nodes);
        Assert.DoesNotContain("::part::", nodes[0].Id);
    }

    [Fact]
    public async Task CjkSection_ThatUsedToOverflowTheBackend_NowSplits()
    {
        // ~1500 CJK characters: comfortably under the old 4000-char rule (so it was emitted whole and then
        // truncated by the backend), and comfortably over a 1000-token budget (so it now splits).
        var paragraphs = Enumerable.Repeat(new string('文', 300), 5);
        var (nodes, _) = await ParseAsync($"# 章\n\n{string.Join("\n\n", paragraphs)}\n");

        Assert.True(nodes.Count > 1,
            "a CJK section over the token budget must split — under the old character rule it was silently truncated by the backend");
        Assert.Contains(nodes, n => n.Id.Contains("::part::", StringComparison.Ordinal));

        // And every emitted chunk must actually fit, which is the whole point.
        foreach (var n in nodes)
        {
            Assert.True(TokenBudget.Fits(n.Content, TokenBudget.SectionBudgetTokens + 200),
                $"chunk '{n.Name}' still exceeds the budget ({TokenBudget.Estimate(n.Content)} tokens)");
        }
    }

    [Fact]
    public void EmbeddingBody_IsTrimmedByTokens_SoCjkKeepsFewerCharsThanEnglish()
    {
        // Parser and embedder now measure in the same unit. A Chinese body costs ~4× the tokens per character,
        // so it must survive ~4× fewer characters — which a flat 1500-char cap got exactly wrong. The English
        // body is real words: since #173 a single 20 000-character run is charged per character (that is a blob,
        // not English), so it would cost the same as CJK and prove nothing.
        var englishSource = string.Join(" ", Enumerable.Repeat("the graph links code", 1000));
        var english = EmbeddingTextBuilder.HeadAndTailWithinTokens(englishSource, TokenBudget.EmbeddingBodyTokens);
        var chinese = EmbeddingTextBuilder.HeadAndTailWithinTokens(new string('文', 20_000), TokenBudget.EmbeddingBodyTokens);

        Assert.True(TokenBudget.Fits(english, TokenBudget.EmbeddingBodyTokens));
        Assert.True(TokenBudget.Fits(chinese, TokenBudget.EmbeddingBodyTokens));
        Assert.True(chinese.Length < english.Length,
            "a CJK body must be cut to fewer characters than an English one, because it costs more tokens per character");
    }

    // ---- #112: CONTAINS follows the heading hierarchy -------------------------------------------------

    [Fact]
    public async Task Sections_NestByHeadingLevel_NotAsAFlatFanOut()
    {
        var (nodes, edges) = await ParseAsync(
            "# Doc\n\nIntro.\n\n## Chapter\n\nFraming.\n\n### Detail\n\nSpecifics.\n");

        string Id(string name) => nodes.Single(n => n.Name == name).Id;
        var contains = edges.Where(e => e.Relationship == "CONTAINS").ToList();

        // Only the top-level heading hangs off the file...
        Assert.Contains(contains, e => e.SourceId == "/docs/x.md" && e.TargetId == Id("Doc"));
        Assert.DoesNotContain(contains, e => e.SourceId == "/docs/x.md" && e.TargetId == Id("Chapter"));
        Assert.DoesNotContain(contains, e => e.SourceId == "/docs/x.md" && e.TargetId == Id("Detail"));

        // ...and each deeper heading is a child of the one that encloses it. A query matching "Specifics"
        // can now reach "Framing" in one hop, which was the whole point of #112.
        Assert.Contains(contains, e => e.SourceId == Id("Doc") && e.TargetId == Id("Chapter"));
        Assert.Contains(contains, e => e.SourceId == Id("Chapter") && e.TargetId == Id("Detail"));
    }

    [Fact]
    public async Task Nesting_SurvivesASkippedHeadingLevel()
    {
        // ## → #### with no ### in between. The #### attaches to the ## that actually encloses it, rather
        // than to a level-3 heading that never existed.
        var (nodes, edges) = await ParseAsync("## Chapter\n\nA.\n\n#### Deep\n\nB.\n");

        string Id(string name) => nodes.Single(n => n.Name == name).Id;
        Assert.Contains(edges, e => e.Relationship == "CONTAINS" && e.SourceId == Id("Chapter") && e.TargetId == Id("Deep"));
    }

    [Fact]
    public async Task Siblings_AtTheSameLevel_DoNotContainEachOther()
    {
        var (nodes, edges) = await ParseAsync("## One\n\nA.\n\n## Two\n\nB.\n");

        string Id(string name) => nodes.Single(n => n.Name == name).Id;
        var contains = edges.Where(e => e.Relationship == "CONTAINS").ToList();

        Assert.DoesNotContain(contains, e => e.SourceId == Id("One") && e.TargetId == Id("Two"));
        Assert.Contains(contains, e => e.SourceId == "/docs/x.md" && e.TargetId == Id("One"));
        Assert.Contains(contains, e => e.SourceId == "/docs/x.md" && e.TargetId == Id("Two"));
    }

    [Fact]
    public async Task Nesting_MovesTheEdges_ButNotTheText()
    {
        // The tension #112 posed: nesting the CONTENT would either duplicate a child's text into its parent
        // (double-counted by BM25 in FTS) or break the StartLine..EndLine ↔ Content invariant that makes
        // citations exact. Only the EDGES nest. Each section still stores exactly its own body.
        const string md = "# Doc\n\nIntro.\n\n## Chapter\n\nFraming.\n";
        var (nodes, _) = await ParseAsync(md);
        var lines = md.Split('\n');

        var parent = nodes.Single(n => n.Name == "Doc");
        Assert.DoesNotContain("Framing", parent.Content);  // the child's text is NOT duplicated into the parent

        foreach (var n in nodes)
        {
            var span = string.Join("\n", lines[(n.StartLine!.Value - 1)..n.EndLine!.Value]);
            Assert.Equal(span, n.Content); // the range still covers exactly the stored content
        }
    }
}
