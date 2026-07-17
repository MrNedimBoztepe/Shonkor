// Licensed to Shonkor under the MIT License.

extern alias bench;

using bench::Shonkor.Bench;
using Shonkor.Core.Models;

namespace Shonkor.Tests;

/// <summary>
/// Equalising the baseline's indexed unit, so the 2×2's graph contribution can be decomposed (#189).
///
/// <para>
/// The +9,1 pp the 2×2 attributes to "the graph" is not pure topology. Shonkor's nodes carry AI summaries
/// that keyword-match plain-English intent; raw source chunks do not — which is why the baseline's keyword
/// arm fired on only 10 of 33 queries. So the number really means "topology <b>plus</b> a richer indexed
/// unit", and those are two different claims. Giving the baseline the same summaries isolates the first.
/// </para>
/// <para>
/// A benchmark change that quietly handed the baseline more (or less) than intended would move the very
/// number it exists to decompose, so the enrichment's edges are pinned rather than eyeballed.
/// </para>
/// </summary>
public class EnrichChunksTests
{
    private static RagBaselineBenchmark.Chunk Chunk(int startLine, int endLine, string text = "code", string file = "a.cs") =>
        new() { File = file, Text = text, StartLine = startLine, EndLine = endLine };

    private static GraphNode Node(string summary, int? start, int? end, string file = "a.cs") =>
        new() { Id = $"{file}::n{start}", Name = "N", Type = "Method", FilePath = file, Content = "", Summary = summary, StartLine = start, EndLine = end };

    [Fact]
    public void AChunkGainsTheSummaryOfTheNodeItCovers_AndKeepsItsCode()
    {
        // Appended, not substituted: the chunk must still be the code it was, or the comparison changes two
        // things at once and measures neither.
        var enriched = RagBaselineBenchmark.EnrichChunks(
            [Chunk(0, 39, "public void Hash() {}")],
            [Node("Hashes an API token with SHA-256.", 1, 20)]);

        Assert.Contains("Hashes an API token", enriched[0].Text, StringComparison.Ordinal);
        Assert.Contains("public void Hash() {}", enriched[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AChunkGainsNothingFromANodeInAnotherFile()
    {
        var enriched = RagBaselineBenchmark.EnrichChunks(
            [Chunk(0, 39, "code", "a.cs")],
            [Node("unrelated", 1, 20, "b.cs")]);

        Assert.Equal("code", enriched[0].Text);
    }

    [Fact]
    public void AChunkGainsNothingFromANodeOutsideItsLineRange()
    {
        // Without this, every chunk of a file would collect every summary in it, handing the baseline far
        // more than the graph gives any single node — over-correcting, and understating the topology.
        var enriched = RagBaselineBenchmark.EnrichChunks(
            [Chunk(0, 39)],
            [Node("far away", 500, 520)]);

        Assert.Equal("code", enriched[0].Text);
    }

    [Fact]
    public void AFileLevelNodeWithNoLines_IsNotAttachedToEveryChunk()
    {
        // A whole-file summary on each window is the same over-correction by another route.
        var enriched = RagBaselineBenchmark.EnrichChunks(
            [Chunk(0, 39), Chunk(40, 79)],
            [Node("the whole file", null, null)]);

        Assert.All(enriched, c => Assert.Equal("code", c.Text));
    }

    [Fact]
    public void ANodeWithoutASummary_ContributesNothing()
    {
        // Most nodes are un-enriched until the worker has run; they must not inject blank lines.
        var enriched = RagBaselineBenchmark.EnrichChunks([Chunk(0, 39)], [Node("", 1, 20)]);

        Assert.Equal("code", enriched[0].Text);
    }

    [Fact]
    public void AnEnrichedChunkGetsAFreshHash_SoItsEmbeddingIsNotServedFromThePlainRunsCache()
    {
        // The chunk cache is keyed by content hash. Reusing the plain chunk's hash would silently score the
        // enriched run with the UNenriched embeddings — the decomposition would read as "summaries buy
        // nothing", which is exactly the answer that would let us keep the flattering pitch.
        var plain = Chunk(0, 39, "public void Hash() {}");
        var enriched = RagBaselineBenchmark.EnrichChunks([plain], [Node("Hashes a token.", 1, 20)]);

        Assert.NotEqual(plain.Hash, enriched[0].Hash);
        Assert.NotEmpty(enriched[0].Hash);
    }

    [Fact]
    public void ChunkCountAndSpansAreUnchanged_SoOnlyTheTextDiffersBetweenTheTwoRuns()
    {
        var chunks = new[] { Chunk(0, 39), Chunk(40, 79) };

        var enriched = RagBaselineBenchmark.EnrichChunks(chunks, [Node("s", 1, 20)]);

        Assert.Equal(chunks.Length, enriched.Count);
        Assert.Equal([(0, 39), (40, 79)], enriched.Select(c => (c.StartLine, c.EndLine)));
    }
}
