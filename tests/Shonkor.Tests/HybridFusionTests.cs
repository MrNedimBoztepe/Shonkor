// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Models;
using Shonkor.Core.Services;

namespace Shonkor.Tests;

/// <summary>Unit tests for the deterministic Reciprocal Rank Fusion used by hybrid search.</summary>
public class HybridFusionTests
{
    private static SearchResult R(string id, IReadOnlyList<GraphEdge>? edges = null) =>
        new(new GraphNode { Id = id, Name = id, Type = "Class" }, 0.0, edges ?? []);

    [Fact]
    public void ItemRankedHighInBothLists_WinsOverItemHighInOnlyOne()
    {
        var fts = new[] { R("A"), R("B"), R("C") };       // A best in FTS
        var vec = new[] { R("B"), R("A"), R("D") };       // B best in vector; A second

        var fused = HybridFusion.ReciprocalRankFusion(fts, vec, maxResults: 4);
        var ids = fused.Select(r => r.Node.Id).ToList();

        // A (rank1 + rank2) and B (rank2 + rank1) both beat C (only FTS) and D (only vector).
        Assert.Equal(4, fused.Count);
        Assert.True(ids.IndexOf("A") <= 1);
        Assert.True(ids.IndexOf("B") <= 1);
        Assert.True(ids.IndexOf("C") >= 2);
        Assert.True(ids.IndexOf("D") >= 2);
    }

    [Fact]
    public void RespectsMaxResults_AndDedupesAcrossLists()
    {
        var fts = new[] { R("A"), R("B"), R("C") };
        var vec = new[] { R("A"), R("B"), R("C") };

        var fused = HybridFusion.ReciprocalRankFusion(fts, vec, maxResults: 2);

        Assert.Equal(2, fused.Count);                       // capped
        Assert.Equal(fused.Select(r => r.Node.Id).Distinct().Count(), fused.Count); // no duplicates
    }

    [Fact]
    public void EmptySecondaryList_PreservesPrimaryOrder()
    {
        var fts = new[] { R("A"), R("B"), R("C") };

        var fused = HybridFusion.ReciprocalRankFusion(fts, [], maxResults: 10);

        Assert.Equal(new[] { "A", "B", "C" }, fused.Select(r => r.Node.Id).ToArray());
    }

    [Fact]
    public void ExactScoreTie_RanksDefiningContainerBeforeItsMember()
    {
        // #343: on an exact-name query the defining File/Class and one of its members (e.g. a test Method)
        // can land at an EXACT RRF tie — each is rank 0 of a different list, so both score 1/(60+1). The
        // user wants the defining symbol, so the container must win the tie deterministically.
        static SearchResult Node(string id, string type) =>
            new(new GraphNode { Id = id, Name = id, Type = type }, 0.0, []);

        // The member is FTS rank 0 (inserted first) AND its id sorts first ordinally — so ONLY the
        // definition-before-member tie-break can put the File ahead. Drop that rule and the Method wins.
        var fts = new[] { Node("a/AlphaTests.cs::T::M", "Method") };
        var vec = new[] { Node("z/Zeta.cs", "File") };

        var fused = HybridFusion.ReciprocalRankFusion(fts, vec, maxResults: 2);

        Assert.Equal(2, fused.Count);
        Assert.Equal(fused[0].Score, fused[1].Score, 12); // precondition: it really is an exact tie
        Assert.Equal("File", fused[0].Node.Type);          // the defining container wins the tie
        Assert.Equal("Method", fused[1].Node.Type);
    }

    [Fact]
    public void PrefersTheRicherSearchResult_WhenSameNodeAppearsInBoth()
    {
        var edge = new GraphEdge { SourceId = "A", TargetId = "X", Relationship = "REFERENCES_TYPE" };
        var fts = new[] { R("A", []) };                     // no edges
        var vec = new[] { R("A", new[] { edge }) };          // carries an edge

        var fused = HybridFusion.ReciprocalRankFusion(fts, vec, maxResults: 1);

        Assert.Single(fused);
        Assert.Single(fused[0].RelatedEdges);                // the richer result won
    }
}
