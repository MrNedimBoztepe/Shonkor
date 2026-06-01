namespace Shonkor.Core.Models;

public record GraphNode
{
    public string Id { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;

    public Dictionary<string, string> Properties { get; init; } = new();

    public string Content
    {
        get => Properties.TryGetValue("Content", out var v) || Properties.TryGetValue("content", out v) ? v : string.Empty;
        init => Properties["Content"] = value;
    }

    public string? Metadata
    {
        get => Properties.TryGetValue("Metadata", out var v) || Properties.TryGetValue("metadata", out v) ? v : null;
        init { if (value != null) Properties["Metadata"] = value; }
    }

    public string FilePath
    {
        get => Properties.TryGetValue("FilePath", out var v) || Properties.TryGetValue("filePath", out v) ? v : string.Empty;
        init => Properties["FilePath"] = value;
    }

    public int? StartLine
    {
        get => (Properties.TryGetValue("StartLine", out var v) || Properties.TryGetValue("startLine", out v) || Properties.TryGetValue("lineNumber", out v)) && int.TryParse(v, out var i) ? i : null;
        init { if (value.HasValue) Properties["StartLine"] = value.Value.ToString(); }
    }

    public int? EndLine
    {
        get => (Properties.TryGetValue("EndLine", out var v) || Properties.TryGetValue("endLine", out v)) && int.TryParse(v, out var i) ? i : null;
        init { if (value.HasValue) Properties["EndLine"] = value.Value.ToString(); }
    }

    public string? ContentHash
    {
        get => Properties.TryGetValue("ContentHash", out var v) || Properties.TryGetValue("contentHash", out v) ? v : null;
        init { if (value != null) Properties["ContentHash"] = value; }
    }
}
