// Licensed to Shonkor under the MIT License.

namespace Shonkor.Core.Models;

/// <summary>
/// Represents a single result returned from a knowledge graph search operation.
/// Combines the matched <see cref="GraphNode"/> with a relevance score and
/// its immediately related edges for contextual understanding.
/// </summary>
/// <param name="Node">The graph node that matched the search query.</param>
/// <param name="Score">
/// A relevance score in the range [0.0, 1.0] indicating how closely
/// this node matches the query, where higher values denote stronger matches.
/// </param>
/// <param name="RelatedEdges">
/// A read-only collection of edges connected to <paramref name="Node"/>,
/// providing relationship context for the matched result.
/// </param>
public record SearchResult(
    GraphNode Node,
    double Score,
    IReadOnlyList<GraphEdge> RelatedEdges);
