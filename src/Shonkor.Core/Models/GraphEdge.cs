namespace Shonkor.Core.Models;

public record GraphEdge
{
    public string SourceId { get; init; } = string.Empty;
    public string TargetId { get; init; } = string.Empty;
    public string Relationship { get; init; } = string.Empty;
    
    // Alias to support parsers that use RelationType instead of Relationship
    public string RelationType
    {
        get => Relationship;
        init => Relationship = value;
    }

    public Dictionary<string, string> Properties { get; init; } = new();
}
