namespace Shonkor.Core.Models;

/// <summary>
/// A node in the knowledge graph. Well-known attributes are strongly typed and map to dedicated
/// database columns; parser-specific, dynamic attributes live in <see cref="Properties"/> and are
/// persisted as a JSON metadata blob.
/// </summary>
public record GraphNode
{
    public string Id { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;

    /// <summary>The node's textual content (source snippet, document section, …).</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>Absolute or repository-relative path of the file this node originates from.</summary>
    public string? FilePath { get; init; }

    /// <summary>1-based start line of the node within its file, if applicable.</summary>
    public int? StartLine { get; init; }

    /// <summary>1-based end line of the node within its file, if applicable.</summary>
    public int? EndLine { get; init; }

    /// <summary>SHA256 content hash, used for incremental indexing of File nodes.</summary>
    public string? ContentHash { get; init; }

    /// <summary>An AI-generated semantic summary of the node's business purpose or functionality.</summary>
    public string? Summary { get; set; }

    /// <summary>The numerical vector representation of the node's Summary.</summary>
    public float[]? Embedding { get; set; }

    /// <summary>
    /// Dynamic, parser-specific attributes (e.g. <c>modifiers</c>, <c>returnType</c>,
    /// <c>referencedTypes</c>, <c>status</c>, <c>sitecorePath</c>). Persisted as JSON.
    /// </summary>
    public Dictionary<string, string> Properties { get; init; } = new();
}
