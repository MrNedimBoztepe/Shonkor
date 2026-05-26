// Licensed to LLMBrain under the MIT License.

namespace LLMBrain.Core.Models;

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
/// A breakdown of edge counts grouped by <see cref="GraphEdge.RelationType"/>
/// (e.g., <c>CONTAINS</c>, <c>IMPLEMENTS</c>, <c>CALLS</c>).
/// </param>
public record GraphStatistics(
    int TotalNodes,
    int TotalEdges,
    Dictionary<string, int> NodesByType,
    Dictionary<string, int> EdgesByRelation);
