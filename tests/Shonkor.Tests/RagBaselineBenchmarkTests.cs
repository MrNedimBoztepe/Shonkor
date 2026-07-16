// Licensed to Shonkor under the MIT License.

extern alias bench;

using bench::Shonkor.Bench;

namespace Shonkor.Tests;

/// <summary>
/// The benchmark harness's pure ranking logic (#191).
///
/// <para>
/// The harness produces the numbers the product is sold on, and until now none of that logic had a test —
/// it was verified by running it and reading the output. This project has already paid for that once: the
/// RAG coverage metric silently measured <b>nothing</b> for an unknown period (#157), because nothing
/// asserted that a bare expected name could resolve to a node.
/// </para>
/// <para>
/// These pin the load-bearing behaviours rather than the exact numbers, so they fail on a logic inversion
/// (RRF ordering, a silently empty keyword arm) without breaking every time a golden set is retuned.
/// </para>
/// </summary>
public class RagBaselineBenchmarkTests
{
    private static RagBaselineBenchmark.Chunk Chunk(string text, string file = "a.cs") =>
        new() { File = file, Text = text, StartLine = 1, EndLine = 1 };

    // --- RRF fusion -----------------------------------------------------------------------------------

    [Fact]
    public void Rrf_RanksAnItemBothArmsAgreeOn_AboveOneOnlyASingleArmFound()
    {
        // Item 7 is second on both arms; item 1 is first on one arm and absent from the other. Agreement is
        // the whole point of fusing, so 7 must win — invert the accumulation and this flips.
        var fused = RagBaselineBenchmark.RrfFuse(vectorRank: [1, 7], keywordRank: [9, 7]);

        Assert.Equal(7, fused[0]);
    }

    [Fact]
    public void Rrf_OnIdenticalRankings_PreservesThatOrder()
    {
        var fused = RagBaselineBenchmark.RrfFuse(vectorRank: [3, 1, 2], keywordRank: [3, 1, 2]);

        Assert.Equal([3, 1, 2], fused);
    }

    [Fact]
    public void Rrf_WithOneArmEmpty_DegradesToTheOtherArm_RatherThanReturningNothing()
    {
        // The keyword arm returns empty on a malformed FTS query (see below). Fusion must then still rank
        // the vector arm, not collapse — a silent empty result is the failure mode #157 was.
        var fused = RagBaselineBenchmark.RrfFuse(vectorRank: [5, 2], keywordRank: []);

        Assert.Equal([5, 2], fused);
    }

    // --- Budgeted chunk pick --------------------------------------------------------------------------

    [Fact]
    public void PickWithinBudget_FollowsTheGivenOrder_NotTheChunkListOrder()
    {
        // The order list IS the ranking; picking by list position instead would quietly measure an unranked
        // baseline and flatter it.
        var chunks = new[] { Chunk("aaaa"), Chunk("bbbb"), Chunk("cccc") };

        var (picked, _) = RagBaselineBenchmark.PickWithinBudget(chunks, [2, 0], budgetTokens: 100);

        Assert.Equal(["cccc", "aaaa"], picked.Select(c => c.Text));
    }

    [Fact]
    public void PickWithinBudget_StopsAtTheBudget()
    {
        // 4 chars = 1 token each at CharsPerToken=4. A 2-token budget admits two chunks, not three.
        var chunks = new[] { Chunk("aaaa"), Chunk("bbbb"), Chunk("cccc") };

        var (picked, tokens) = RagBaselineBenchmark.PickWithinBudget(chunks, [0, 1, 2], budgetTokens: 2);

        Assert.Equal(2, picked.Count);
        Assert.Equal(2, tokens);
    }

    [Fact]
    public void PickWithinBudget_AlwaysTakesTheTopHit_EvenWhenItAloneBlowsTheBudget()
    {
        // Returning nothing for an over-budget top hit would score the baseline as a miss on a chunk it
        // actually retrieved — flattering US, which is the direction a benchmark must never round.
        var chunks = new[] { Chunk(new string('x', 400)) };

        var (picked, _) = RagBaselineBenchmark.PickWithinBudget(chunks, [0], budgetTokens: 1);

        Assert.Single(picked);
    }

    // --- Keyword (FTS/BM25) arm -----------------------------------------------------------------------

    [Fact]
    public void KeywordArm_FindsAMatchingChunk_AndReturnsNothingForANonMatchingQuery()
    {
        var chunks = new[] { Chunk("public sealed class TokenHasher"), Chunk("internal record GraphEdge") };
        using var fts = RagBaselineBenchmark.BuildChunkFts(chunks);

        var hit = RagBaselineBenchmark.KeywordRankChunks(fts, "TokenHasher");
        var miss = RagBaselineBenchmark.KeywordRankChunks(fts, "nonexistentsymbolxyz");

        Assert.Equal([0], hit);   // chunk indices, 0-based — the rowid-1 conversion is easy to get wrong
        Assert.Empty(miss);
    }

    [Fact]
    public void KeywordArm_OnAQueryFts5CannotParse_ReturnsEmpty_WhichIsWhyTheArmsFiringRateIsReported()
    {
        // FTS5 rejects bare punctuation; the harness swallows the SqliteException and returns empty. That is
        // deliberate (one unparseable query must not end the run) but it is exactly how an arm can silently
        // contribute nothing — #166 reports the firing rate (10/33) for this reason. Pinned so the swallow
        // stays a known, measured behaviour rather than a surprise.
        var chunks = new[] { Chunk("public sealed class TokenHasher") };
        using var fts = RagBaselineBenchmark.BuildChunkFts(chunks);

        var result = RagBaselineBenchmark.KeywordRankChunks(fts, "\"unbalanced");

        Assert.Empty(result);
    }
}
