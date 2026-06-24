// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;

namespace Shonkor.Infrastructure.Services;

/// <summary>
/// Storage-backed <see cref="IGraphView"/> — a thin read-only adapter that maps the view's indexed access
/// patterns onto the graph store's existing queries, so a post-processor never materialises the whole graph.
/// </summary>
public sealed class StorageBackedGraphView : IGraphView
{
    private readonly IGraphStorageProvider _storage;

    public StorageBackedGraphView(IGraphStorageProvider storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
    }

    public Task<GraphNode?> GetNodeAsync(string id, CancellationToken cancellationToken = default)
        => _storage.GetNodeByIdAsync(id, cancellationToken);

    public Task<IReadOnlyList<GraphNode>> NodesByTypeAsync(string type, CancellationToken cancellationToken = default)
        => _storage.GetNodesByTypesAsync(new[] { type }, cancellationToken);

    public Task<IReadOnlyDictionary<string, IReadOnlyList<GraphNode>>> DefinitionsByNameAsync(IEnumerable<string> names, CancellationToken cancellationToken = default)
        => _storage.GetDefinitionsByNamesAsync(names, cancellationToken);

    public Task<(IReadOnlyList<GraphEdge> Edges, IReadOnlyDictionary<string, GraphNode> Neighbours)> IncidentEdgesAsync(string nodeId, CancellationToken cancellationToken = default)
        => _storage.GetIncidentEdgesAsync(nodeId, cancellationToken);
}
