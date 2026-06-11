// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Models;

namespace Shonkor.Core.Interfaces;

/// <summary>
/// Read-side search and traversal over the knowledge graph: full-text search, vector similarity,
/// and N-hop subgraph expansion. Separated from <see cref="IGraphStore"/> so query-only consumers
/// (e.g. the MCP tools, the dashboard search endpoints) need not depend on write operations.
/// </summary>
public interface IGraphSearch
{
    /// <summary>
    /// Performs a full-text search over the graph nodes using the given query.
    /// Results are ranked by relevance score in descending order.
    /// </summary>
    Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int maxResults = 10, int offset = 0, string? filterType = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a semantic similarity search using vector embeddings.
    /// </summary>
    Task<IReadOnlyList<SearchResult>> SearchSemanticAsync(float[] queryEmbedding, int maxResults = 10, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a subgraph by expanding outward from the specified seed nodes,
    /// by the given number of hops along edges in either direction.
    /// </summary>
    Task<(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)> GetSubgraphAsync(IEnumerable<string> seedNodeIds, int hops = 1, CancellationToken cancellationToken = default);
}
