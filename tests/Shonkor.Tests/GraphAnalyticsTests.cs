// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Models;
using Shonkor.Core.Services;

namespace Shonkor.Tests;

/// <summary>
/// Tests the self-contained topology analytics (Brandes betweenness + degree) against graphs whose exact
/// centrality is known by hand.
/// </summary>
public class GraphAnalyticsTests
{
    private static GraphNode N(string id) => new() { Id = id, Name = id, Type = "Class" };
    private static GraphNode NE(string id, params float[] embedding) => new() { Id = id, Name = id, Type = "Class", Embedding = embedding };
    private static GraphEdge E(string s, string t, string rel = "CALLS") => new() { SourceId = s, TargetId = t, Relationship = rel };

    [Fact]
    public void Centrality_PathGraph_InternalNodesAreTheBrokers()
    {
        // A — B — C — D. Internal nodes B and C each broker exactly 2 shortest-path pairs; ends broker none.
        var nodes = new[] { N("A"), N("B"), N("C"), N("D") };
        var edges = new[] { E("A", "B"), E("B", "C"), E("C", "D") };

        var scores = GraphAnalytics.Centrality(nodes, edges).ToDictionary(s => s.NodeId);

        Assert.Equal(2.0, scores["B"].Betweenness, 3);
        Assert.Equal(2.0, scores["C"].Betweenness, 3);
        Assert.Equal(0.0, scores["A"].Betweenness, 3);
        Assert.Equal(0.0, scores["D"].Betweenness, 3);
        Assert.Equal(1, scores["A"].Degree);
        Assert.Equal(2, scores["B"].Degree);
    }

    [Fact]
    public void Centrality_StarGraph_CentreBrokersEveryPair()
    {
        // Centre S with 3 leaves: every leaf-to-leaf shortest path goes through S → C(3,2) = 3.
        var nodes = new[] { N("S"), N("L1"), N("L2"), N("L3") };
        var edges = new[] { E("S", "L1"), E("S", "L2"), E("S", "L3") };

        var scores = GraphAnalytics.Centrality(nodes, edges).ToDictionary(s => s.NodeId);

        Assert.Equal(3.0, scores["S"].Betweenness, 3);
        Assert.Equal(3, scores["S"].Degree);
        Assert.All(new[] { "L1", "L2", "L3" }, l => Assert.Equal(0.0, scores[l].Betweenness, 3));
    }

    [Fact]
    public void Centrality_ExcludesStructuralEdges_ByDefault()
    {
        // A CONTAINS edge from A to D must NOT create a shortcut that changes brokerage on the A-B-C-D path.
        var nodes = new[] { N("A"), N("B"), N("C"), N("D") };
        var edges = new[] { E("A", "B"), E("B", "C"), E("C", "D"), E("A", "D", "CONTAINS") };

        var scores = GraphAnalytics.Centrality(nodes, edges).ToDictionary(s => s.NodeId);

        // Same as the pure path — the structural edge was ignored.
        Assert.Equal(2.0, scores["B"].Betweenness, 3);
        Assert.Equal(2.0, scores["C"].Betweenness, 3);
        Assert.Equal(1, scores["A"].Degree); // only A-B counts, not the excluded A-D CONTAINS
    }

    [Fact]
    public void Centrality_DanglingEdges_AreIgnored()
    {
        // An edge to a node not in the set must not throw or affect scores.
        var nodes = new[] { N("A"), N("B") };
        var edges = new[] { E("A", "B"), E("B", "GHOST") };

        var scores = GraphAnalytics.Centrality(nodes, edges).ToDictionary(s => s.NodeId);

        Assert.Equal(0.0, scores["A"].Betweenness, 3);
        Assert.Equal(1, scores["B"].Degree);
    }

    [Fact]
    public void DetectCommunities_TwoDisconnectedTriangles_AreSeparateClusters()
    {
        // Two triangles with NO edge between them → two connected components = two clusters.
        var nodes = new[] { N("A"), N("B"), N("C"), N("D"), N("E"), N("F") };
        var edges = new[]
        {
            E("A", "B"), E("B", "C"), E("A", "C"),   // triangle 1
            E("D", "E"), E("E", "F"), E("D", "F")    // triangle 2 (disconnected)
        };

        var comm = GraphAnalytics.DetectCommunities(nodes, edges);

        Assert.Equal(comm["A"], comm["B"]);
        Assert.Equal(comm["B"], comm["C"]);
        Assert.Equal(comm["D"], comm["E"]);
        Assert.Equal(comm["E"], comm["F"]);
        Assert.NotEqual(comm["A"], comm["D"]);
        Assert.Equal(2, comm.Values.Distinct().Count());
    }

    [Fact]
    public void DetectCommunities_BridgedClusters_AreOneComponent()
    {
        // Two triangles joined by a single bridge C—D are ONE connected component (component clustering does
        // not split a connected graph — that would need modularity-based detection, a future enhancement).
        var nodes = new[] { N("A"), N("B"), N("C"), N("D"), N("E"), N("F") };
        var edges = new[]
        {
            E("A", "B"), E("B", "C"), E("A", "C"),
            E("D", "E"), E("E", "F"), E("D", "F"),
            E("C", "D")
        };

        var comm = GraphAnalytics.DetectCommunities(nodes, edges);
        Assert.Single(comm.Values.Distinct());
    }

    [Fact]
    public void DetectCommunities_DisconnectedClusters_AndIsolatedNode()
    {
        // Two disconnected edges + one isolated node → three communities.
        var nodes = new[] { N("A"), N("B"), N("C"), N("D"), N("ISO") };
        var edges = new[] { E("A", "B"), E("C", "D") };

        var comm = GraphAnalytics.DetectCommunities(nodes, edges);

        Assert.Equal(comm["A"], comm["B"]);
        Assert.Equal(comm["C"], comm["D"]);
        Assert.NotEqual(comm["A"], comm["C"]);
        Assert.NotEqual(comm["A"], comm["ISO"]);
        Assert.Equal(3, comm.Values.Distinct().Count());
    }

    [Fact]
    public void DetectCommunities_IsDeterministic_AcrossRuns()
    {
        var nodes = new[] { N("A"), N("B"), N("C"), N("D"), N("E"), N("F") };
        var edges = new[] { E("A", "B"), E("B", "C"), E("A", "C"), E("D", "E"), E("E", "F"), E("D", "F"), E("C", "D") };

        var first = GraphAnalytics.DetectCommunities(nodes, edges);
        var second = GraphAnalytics.DetectCommunities(nodes, edges);

        Assert.Equal(first, second);
    }

    [Fact]
    public void SurprisingConnections_SimilarButUnlinked_AreFound_LinkedPairsExcluded()
    {
        // A and B have identical embeddings but no edge → surprising. A—C are similar AND linked → excluded.
        // D is orthogonal to everything → never surprising.
        var nodes = new[]
        {
            NE("A", 1f, 0f, 0f),
            NE("B", 1f, 0f, 0f),
            NE("C", 0.99f, 0.1f, 0f),
            NE("D", 0f, 0f, 1f)
        };
        var edges = new[] { E("A", "C") };

        var sc = GraphAnalytics.SurprisingConnections(nodes, edges, minSimilarity: 0.9);

        Assert.Contains(sc, r => r.SourceId == "A" && r.TargetId == "B");   // similar + unlinked
        Assert.DoesNotContain(sc, r => r.SourceId == "A" && r.TargetId == "C"); // similar but already linked
        Assert.DoesNotContain(sc, r => r.SourceId == "D" || r.TargetId == "D"); // orthogonal
        Assert.All(sc, r => Assert.True(r.Similarity >= 0.9));
    }

    [Fact]
    public void SurprisingConnections_NoEmbeddings_ReturnsEmpty()
    {
        var nodes = new[] { N("A"), N("B") };
        var edges = Array.Empty<GraphEdge>();

        Assert.Empty(GraphAnalytics.SurprisingConnections(nodes, edges));
    }

    [Fact]
    public void DetectModularityCommunities_TwoTrianglesBridged_SplitIntoTwo()
    {
        // The case connected-components CANNOT split: two dense triangles joined by ONE bridge C—D.
        // Modularity keeps the triangles as separate communities (intra-coupling ≫ the single inter-edge).
        var nodes = new[] { N("A"), N("B"), N("C"), N("D"), N("E"), N("F") };
        var edges = new[]
        {
            E("A", "B"), E("B", "C"), E("A", "C"),   // triangle 1
            E("D", "E"), E("E", "F"), E("D", "F"),   // triangle 2
            E("C", "D")                              // bridge
        };

        var comm = GraphAnalytics.DetectModularityCommunities(nodes, edges);

        Assert.Equal(comm["A"], comm["B"]);
        Assert.Equal(comm["B"], comm["C"]);
        Assert.Equal(comm["D"], comm["E"]);
        Assert.Equal(comm["E"], comm["F"]);
        Assert.NotEqual(comm["A"], comm["D"]);
        Assert.Equal(2, comm.Values.Distinct().Count());
    }

    [Fact]
    public void DetectModularityCommunities_IsDeterministic_AcrossRuns()
    {
        var nodes = new[] { N("A"), N("B"), N("C"), N("D"), N("E"), N("F") };
        var edges = new[] { E("A", "B"), E("B", "C"), E("A", "C"), E("D", "E"), E("E", "F"), E("D", "F"), E("C", "D") };

        Assert.Equal(
            GraphAnalytics.DetectModularityCommunities(nodes, edges),
            GraphAnalytics.DetectModularityCommunities(nodes, edges));
    }

    [Fact]
    public void DetectModularityCommunities_DisconnectedPieces_StaySeparate()
    {
        // Two disconnected edges + one isolated node → three communities.
        var nodes = new[] { N("A"), N("B"), N("C"), N("D"), N("ISO") };
        var edges = new[] { E("A", "B"), E("C", "D") };

        var comm = GraphAnalytics.DetectModularityCommunities(nodes, edges);

        Assert.Equal(comm["A"], comm["B"]);
        Assert.Equal(comm["C"], comm["D"]);
        Assert.NotEqual(comm["A"], comm["C"]);
        Assert.Equal(3, comm.Values.Distinct().Count());
    }

    [Fact]
    public void DetectModularityCommunities_Empty_ReturnsEmpty()
    {
        Assert.Empty(GraphAnalytics.DetectModularityCommunities(Array.Empty<GraphNode>(), Array.Empty<GraphEdge>()));
    }
}
