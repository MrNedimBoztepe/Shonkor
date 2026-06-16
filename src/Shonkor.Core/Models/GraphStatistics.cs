// Licensed to Shonkor under the MIT License.

namespace Shonkor.Core.Models;

/// <summary>
/// Provides aggregate statistics about the current state of the knowledge graph.
/// Useful for monitoring, diagnostics, and reporting on graph health.
/// </summary>
/// <param name="TotalNodes">The total number of nodes in the graph.</param>
/// <param name="TotalEdges">The total number of edges in the graph.</param>
/// <param name="NodesByType">
/// A breakdown of node counts grouped by <see cref="GraphNode.Type"/>
/// (e.g., <c>Class</c>, <c>Method</c>, <c>File</c>).
/// </param>
/// <param name="EdgesByRelation">
/// A breakdown of edge counts grouped by <see cref="GraphEdge.Relationship"/>
/// (e.g., <c>CONTAINS</c>, <c>IMPLEMENTS</c>, <c>CALLS</c>).
/// </param>
/// <param name="SchemeVersion">
/// The node-id scheme version this graph was built under (0 for unstamped legacy graphs).
/// </param>
/// <param name="CurrentSchemeVersion">
/// The node-id scheme version the running code produces (<see cref="Services.CsharpNodeId.SchemeVersion"/>).
/// </param>
public record GraphStatistics(
    long TotalNodes,
    long TotalEdges,
    Dictionary<string, int> NodesByType,
    Dictionary<string, int> EdgesByRelation,
    int SchemeVersion = 0,
    int CurrentSchemeVersion = 0)
{
    /// <summary>
    /// True when the graph is non-empty but was built under an older node-id scheme than the running code
    /// produces — its method/constructor ids are in an outdated format and a full re-index is recommended.
    /// </summary>
    public bool ReindexRecommended => TotalNodes > 0 && SchemeVersion < CurrentSchemeVersion;
}
