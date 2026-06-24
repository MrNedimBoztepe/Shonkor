// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Models;

namespace Shonkor.Core.Interfaces;

/// <summary>
/// A read-only, storage-backed view of the assembled graph handed to an <see cref="IGraphPostProcessor"/>.
/// Exposes only indexed access patterns (by id, by type, by definition name, a node's incident edges) so a
/// post-processor can query across the whole graph without the host materialising it in memory. Async
/// because the backing store is async; broader query surfaces are added as features (F3/F8) need them.
/// </summary>
public interface IGraphView
{
    /// <summary>The node with the given id, or <c>null</c> if none exists (the basis for unresolved-reference checks).</summary>
    Task<GraphNode?> GetNodeAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>All nodes of a given <see cref="GraphNode.Type"/>.</summary>
    Task<IReadOnlyList<GraphNode>> NodesByTypeAsync(string type, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the given (simple) names to C# definition nodes (Class/Interface/Record/Struct/Enum), keyed
    /// by name — the basis for reference resolvers (e.g. a config type string → its declaring class node).
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<GraphNode>>> DefinitionsByNameAsync(IEnumerable<string> names, CancellationToken cancellationToken = default);

    /// <summary>The edges incident to a node (where it is source or target), with the other-end nodes keyed by id.</summary>
    Task<(IReadOnlyList<GraphEdge> Edges, IReadOnlyDictionary<string, GraphNode> Neighbours)> IncidentEdgesAsync(string nodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// All edges of a given <see cref="GraphEdge.Relationship"/> across the whole graph — the basis for
    /// structural rules that reason over a relationship globally (e.g. Helix layer-dependency checks over
    /// the C# coupling edges). Returns dangling edges too; resolve endpoints via <see cref="GetNodeAsync"/>.
    /// </summary>
    Task<IReadOnlyList<GraphEdge>> EdgesByRelationshipAsync(string relationship, CancellationToken cancellationToken = default);
}
