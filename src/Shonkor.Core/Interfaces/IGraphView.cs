// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Models;

namespace Shonkor.Core.Interfaces;

/// <summary>
/// A read-only view of the assembled graph handed to an <see cref="IGraphPostProcessor"/>. Implementations
/// are storage-backed (indexed queries), so a post-processor can look across the whole graph without the
/// host materialising every node/edge in memory. Only indexed access patterns are exposed — there is
/// deliberately no "give me everything" enumeration.
/// </summary>
public interface IGraphView
{
    /// <summary>The node with the given id, or <c>null</c> if none exists (the basis for unresolved-reference checks).</summary>
    GraphNode? GetNode(string id);

    /// <summary>All nodes of a given <see cref="GraphNode.Type"/>.</summary>
    IEnumerable<GraphNode> NodesByType(string type);

    /// <summary>Nodes whose dynamic <see cref="GraphNode.Properties"/> contain <paramref name="key"/> == <paramref name="value"/> (e.g. a C# type by its simple name).</summary>
    IEnumerable<GraphNode> NodesByProperty(string key, string value);

    /// <summary>Edges originating at the given node id.</summary>
    IEnumerable<GraphEdge> EdgesFrom(string sourceId);

    /// <summary>Edges pointing at the given node id.</summary>
    IEnumerable<GraphEdge> EdgesTo(string targetId);

    /// <summary>All edges with the given <see cref="GraphEdge.Relationship"/> (e.g. all <c>REFERENCES</c> edges).</summary>
    IEnumerable<GraphEdge> EdgesByRelationship(string relationship);
}
