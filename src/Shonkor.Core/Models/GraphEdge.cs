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

    /// <summary>
    /// The trust tier of this edge (how it was established). Defaults to <see cref="Provenance.Extracted"/>
    /// so deterministic parsers need not set it; heuristic/cross-tech/LLM sources must downgrade to
    /// <see cref="Provenance.Inferred"/> (or <see cref="Provenance.Ambiguous"/>). Persisted as a dedicated
    /// column; edges from graphs created before this column read back as <see cref="Provenance.Extracted"/>.
    /// </summary>
    public Provenance Provenance { get; init; } = Provenance.Extracted;

    /// <summary>Dynamic, parser-specific attributes of the relationship.</summary>
    public Dictionary<string, string> Properties { get; init; } = new();
}
