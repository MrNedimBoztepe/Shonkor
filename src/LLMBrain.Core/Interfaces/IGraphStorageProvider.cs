// Licensed to LLMBrain under the MIT License.

using LLMBrain.Core.Models;

namespace LLMBrain.Core.Interfaces;

/// <summary>
/// Defines the contract for a persistent knowledge graph storage backend.
/// Supports CRUD operations on nodes and edges, semantic search, and subgraph traversal.
/// </summary>
public interface IGraphStorageProvider
{
    /// <summary>
    /// Initializes the storage backend, creating required schemas, collections,
    /// and indexes if they do not already exist.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts or updates the specified nodes in the graph store.
    /// Existing nodes with matching <see cref="GraphNode.Id"/> values are replaced.
    /// </summary>
    Task UpsertNodesAsync(IEnumerable<GraphNode> nodes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts or updates the specified edges in the graph store.
    /// Existing edges with matching source/target/relation combinations are replaced.
    /// </summary>
    Task UpsertEdgesAsync(IEnumerable<GraphEdge> edges, CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a semantic search over the graph nodes using the given natural-language query.
    /// Results are ranked by relevance score in descending order.
    /// </summary>
    Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int maxResults = 10, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a subgraph centered on the specified seed nodes, expanding outward
    /// by the given number of hops along edges in either direction.
    /// </summary>
    Task<(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)> GetSubgraphAsync(IEnumerable<string> seedNodeIds, int hops = 1, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all nodes and associated edges that originate from the specified file path.
    /// Used during incremental re-indexing to remove stale data before re-parsing.
    /// </summary>
    Task DeleteByFilePathAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns aggregate statistics about the current state of the knowledge graph.
    /// </summary>
    Task<GraphStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);
}
