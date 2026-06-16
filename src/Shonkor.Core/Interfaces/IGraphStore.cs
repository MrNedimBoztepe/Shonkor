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
    /// Deletes all nodes and associated edges for many file paths in a single transaction. Preferred
    /// over looping <see cref="DeleteByFilePathAsync"/> when clearing a large changeset (first index,
    /// branch switch, bulk re-scan), where per-file transactions dominate the cost.
    /// </summary>
    Task DeleteByFilePathsAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears a file's nodes and its OUTGOING/internal edges, but PRESERVES edges that point into the
    /// file from other files (incoming references). Used by single-file re-indexing so an edit doesn't
    /// drop a symbol's incoming references (which other files own and which only a whole-graph cross-tech
    /// relink would otherwise restore). Re-parsing recreates the file's symbols with the same ids, so the
    /// preserved incoming edges stay valid.
    /// </summary>
    Task ClearFileForReindexAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all File node paths currently stored in the graph.
    /// </summary>
    Task<IReadOnlyList<string>> GetAllIndexedFilePathsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all nodes whose <see cref="GraphNode.FilePath"/> equals <paramref name="filePath"/>
    /// (the symbols declared in that file). Used by the scoped relink after a single-file re-index.
    /// </summary>
    Task<IReadOnlyList<GraphNode>> GetNodesByFilePathAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the given type names to their definition nodes (Class/Interface/Record/Struct/Enum),
    /// keyed by name (a name may map to several same-named definitions across namespaces/files). Lets a
    /// scoped relink resolve only the names a file references, instead of loading the whole graph.
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<GraphNode>>> GetDefinitionsByNamesAsync(IEnumerable<string> names, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Returns the node-id scheme version this graph was built under (see
    /// <see cref="Services.CsharpNodeId.SchemeVersion"/>). A value below the current version means the graph
    /// holds ids in an outdated format and a full re-index is recommended. Unstamped legacy graphs read 0.
    /// </summary>
    Task<int> GetNodeIdSchemeVersionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stamps the graph with the node-id scheme <paramref name="version"/>. Called after a full scan, once
    /// the whole graph has been (re)built under that scheme.
    /// </summary>
    Task SetNodeIdSchemeVersionAsync(int version, CancellationToken cancellationToken = default);
}
