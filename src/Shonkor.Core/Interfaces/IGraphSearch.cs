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

    /// <summary>
    /// Returns the edges directly incident to a single node — those where it is the source or the
    /// target — together with the nodes on the other end of each edge, keyed by id. Cheaper and more
    /// precise than a 1-hop <see cref="GetSubgraphAsync"/> when only a node's direct dependents or
    /// dependencies are needed: it touches just the node's own edges instead of materializing and
    /// filtering its whole neighbourhood.
    /// </summary>
    Task<(IReadOnlyList<GraphEdge> Edges, IReadOnlyDictionary<string, GraphNode> Neighbours)> GetIncidentEdgesAsync(string nodeId, CancellationToken cancellationToken = default);
}
