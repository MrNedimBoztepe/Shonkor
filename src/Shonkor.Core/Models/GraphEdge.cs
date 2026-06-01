namespace Shonkor.Core.Models;

/// <summary>
/// A directed relationship between two <see cref="GraphNode"/> instances.
/// </summary>
public record GraphEdge
{
    public string SourceId { get; init; } = string.Empty;
    public string TargetId { get; init; } = string.Empty;

    /// <summary>The relationship kind, e.g. <c>CONTAINS</c>, <c>IMPLEMENTS</c>, <c>REFERENCES_TYPE</c>.</summary>
    public string Relationship { get; init; } = string.Empty;

    /// <summary>Dynamic, parser-specific attributes of the relationship.</summary>
    public Dictionary<string, string> Properties { get; init; } = new();
}
