// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Models;

namespace Shonkor.Core.Interfaces;

/// <summary>
/// Core persistence of the knowledge graph: lifecycle, writes, and direct reads of nodes and edges.
/// Does not include search/traversal (see <see cref="IGraphSearch"/>) or AI enrichment
/// (see <see cref="ISemanticGraphStore"/>), so callers can depend on only what they use.
/// </summary>
public interface IGraphStore
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
    /// Deletes all nodes and associated edges that originate from the specified file path.
    /// Used during incremental re-indexing to remove stale data before re-parsing.
    /// </summary>
    Task DeleteByFilePathAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all File node paths currently stored in the graph.
    /// </summary>
    Task<IReadOnlyList<string>> GetAllIndexedFilePathsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the stored content hash for each of the given File node IDs (paths) that exist,
    /// keyed by node ID. Used for fast incremental-scan change detection without loading content.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetContentHashesAsync(IEnumerable<string> fileIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns aggregate statistics about the current state of the knowledge graph.
    /// </summary>
    Task<GraphStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all nodes from the graph store.
    /// </summary>
    Task<IReadOnlyList<GraphNode>> GetAllNodesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves nodes filtered by a set of types.
    /// </summary>
    Task<IReadOnlyList<GraphNode>> GetNodesByTypesAsync(IEnumerable<string> types, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a single node by its identifier, or <c>null</c> if it does not exist.
    /// </summary>
    Task<GraphNode?> GetNodeByIdAsync(string id, CancellationToken cancellationToken = default);
}
