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
    /// As <see cref="SearchSemanticAsync(float[], int, CancellationToken)"/>, but drops hits scoring below
    /// <paramref name="minSimilarity"/> (cosine, 0..1) so noise matches at low similarity are not returned as
    /// if they were relevant. A floor of 0 keeps every hit.
    /// </summary>
    Task<IReadOnlyList<SearchResult>> SearchSemanticAsync(float[] queryEmbedding, int maxResults, double minSimilarity, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Returns every edge of a given relationship type across the whole graph. Used by global structural
    /// analyses (e.g. Helix layer-dependency checks over the C# coupling relationships) that reason over a
    /// relationship as a whole rather than around a single node.
    /// </summary>
    Task<IReadOnlyList<GraphEdge>> GetEdgesByRelationshipAsync(string relationship, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every edge in the whole graph. Used by global topology analyses (centrality, community
    /// detection) that need the full edge set at once, rather than the per-node or per-relationship views.
    /// </summary>
    Task<IReadOnlyList<GraphEdge>> GetAllEdgesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns up to <paramref name="limit"/> nodes that carry an embedding vector (the vector populated),
    /// for embedding-based analyses such as surprising-connection detection. A non-positive limit means all.
    /// </summary>
    Task<IReadOnlyList<GraphNode>> GetNodesWithEmbeddingsAsync(int limit, CancellationToken cancellationToken = default);
}
