using System.Collections.Frozen;

using LLMBrain.Core.Interfaces;
using LLMBrain.Core.Models;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LLMBrain.Core.Services;

/// <summary>
/// Parses YAML configuration files to extract Sitecore SCS (Serialized Content as Code) items.
/// Extracts template definitions, parent relationships, and template instances.
/// </summary>
public sealed class CmsConfigParser : IFileParser
{
    private const string IdField = "ID";
    private const string TemplateField = "Template";
    private const string ParentField = "Parent";
    private const string PathField = "Path";
    private const string NameField = "Name";

    /// <inheritdoc />
    public IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".yml", ".yaml" }.ToFrozenSet();

    /// <inheritdoc />
    public Task<(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)> ParseAsync(
        string filePath,
        string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(content);

        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();

        var item = DeserializeYaml(content);
        if (item is null)
        {
            return Task.FromResult<(IReadOnlyList<GraphNode>, IReadOnlyList<GraphEdge>)>(
                (nodes.AsReadOnly(), edges.AsReadOnly()));
        }

        BuildGraphFromSitecoreItem(filePath, item, nodes, edges);

        return Task.FromResult<(IReadOnlyList<GraphNode>, IReadOnlyList<GraphEdge>)>(
            (nodes.AsReadOnly(), edges.AsReadOnly()));
    }

    /// <summary>
    /// Deserializes the YAML content into a dictionary representation.
    /// Returns <c>null</c> if the content cannot be parsed or is not a valid dictionary.
    /// </summary>
    private static Dictionary<string, object>? DeserializeYaml(string content)
    {
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(PascalCaseNamingConvention.Instance)
                .Build();

            return deserializer.Deserialize<Dictionary<string, object>>(content);
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds graph nodes and edges from a deserialized Sitecore SCS item.
    /// Extracts the item ID, template, parent, and path to create a <c>SitecoreTemplate</c> node
    /// with <c>CHILD_OF</c> and <c>INSTANCE_OF</c> relationships.
    /// </summary>
    private static void BuildGraphFromSitecoreItem(
        string filePath,
        Dictionary<string, object> item,
        List<GraphNode> nodes,
        List<GraphEdge> edges)
    {
        var id = GetStringValue(item, IdField);
        var template = GetStringValue(item, TemplateField);
        var parent = GetStringValue(item, ParentField);
        var path = GetStringValue(item, PathField);
        var name = GetStringValue(item, NameField);

        // Use the Sitecore item ID as the node ID, falling back to file path
        var nodeId = !string.IsNullOrWhiteSpace(id) ? id : filePath;
        var displayName = !string.IsNullOrWhiteSpace(name)
            ? name
            : Path.GetFileNameWithoutExtension(filePath);

        var properties = new Dictionary<string, string>
        {
            ["filePath"] = filePath
        };

        if (!string.IsNullOrWhiteSpace(id))
        {
            properties["sitecoreId"] = id;
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            properties["sitecorePath"] = path;
        }

        if (!string.IsNullOrWhiteSpace(template))
        {
            properties["template"] = template;
        }

        nodes.Add(new GraphNode
        {
            Id = nodeId,
            Name = displayName,
            Type = "SitecoreTemplate",
            Properties = properties
        });

        // CHILD_OF edge: this item is a child of its parent
        if (!string.IsNullOrWhiteSpace(parent))
        {
            edges.Add(new GraphEdge
            {
                SourceId = nodeId,
                TargetId = parent,
                Relationship = "CHILD_OF"
            });
        }

        // INSTANCE_OF edge: this item is an instance of its template
        if (!string.IsNullOrWhiteSpace(template))
        {
            edges.Add(new GraphEdge
            {
                SourceId = nodeId,
                TargetId = template,
                Relationship = "INSTANCE_OF"
            });
        }
    }

    /// <summary>
    /// Safely extracts a string value from the YAML dictionary by key.
    /// Returns <c>null</c> if the key is missing or the value is not a string.
    /// </summary>
    private static string? GetStringValue(Dictionary<string, object> dictionary, string key) =>
        dictionary.TryGetValue(key, out var value) ? value?.ToString() : null;
}
