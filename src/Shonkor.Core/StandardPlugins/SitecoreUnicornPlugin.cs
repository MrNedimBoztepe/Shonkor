using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using YamlDotNet.Serialization;

namespace Shonkor.Plugins;

public sealed class SitecoreUnicornPlugin : IFileParser
{
    private const string IdField = "ID";
    private const string TemplateField = "Template";
    private const string ParentField = "Parent";
    private const string PathField = "Path";
    private const string NameField = "Name";

    // Regex to detect Sitecore GUIDs. Handles typical curly brace format, plain format, and pipe separation.
    private static readonly Regex GuidRegex = new Regex(@"[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}", RegexOptions.Compiled);

    public IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".yml", ".yaml" }.ToFrozenSet();

    public IReadOnlyList<NodeTypeDescriptor> NodeTypeDescriptors { get; } = new[]
    {
        new NodeTypeDescriptor("SitecoreItem", "CMS", true)
    };

    public Task<(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)> ParseAsync(string filePath, string content)
    {
        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();

        if (!content.Contains("Sitecore", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<(IReadOnlyList<GraphNode>, IReadOnlyList<GraphEdge>)>((nodes, edges));
        }

        try
        {
            var deserializer = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();
            var doc = deserializer.Deserialize<Dictionary<string, object>>(content);

            if (doc != null && doc.TryGetValue(IdField, out var idObj))
            {
                var itemId = idObj as string;
                if (!string.IsNullOrEmpty(itemId))
                {
                    var itemName = GetItemName(doc, filePath);
                    
                    var properties = new Dictionary<string, string>
                    {
                        ["cms"] = "Sitecore Unicorn"
                    };

                    // Extract Relationships from Root
                    if (doc.TryGetValue(TemplateField, out var tmplObj) && tmplObj is string templateId && !string.IsNullOrEmpty(templateId))
                    {
                        edges.Add(new GraphEdge { SourceId = itemId, TargetId = templateId, Relationship = "BASED_ON_TEMPLATE" });
                        properties["TemplateId"] = templateId;
                    }

                    if (doc.TryGetValue(ParentField, out var parentObj) && parentObj is string parentId && !string.IsNullOrEmpty(parentId))
                    {
                        edges.Add(new GraphEdge { SourceId = parentId, TargetId = itemId, Relationship = "HAS_CHILD" });
                        properties["ParentId"] = parentId;
                    }

                    if (doc.TryGetValue(PathField, out var pathObj) && pathObj is string pathStr)
                    {
                        properties["SitecorePath"] = pathStr;
                    }

                    // Process Fields
                    ProcessFields(doc, "SharedFields", null, itemId, properties, edges);

                    if (doc.TryGetValue("Languages", out var languagesObj) && languagesObj is List<object> languages)
                    {
                        foreach (var langObj in languages)
                        {
                            if (langObj is Dictionary<object, object> langDict && langDict.TryGetValue("Language", out var langNameObj))
                            {
                                var langName = langNameObj?.ToString() ?? "unknown";
                                if (langDict.TryGetValue("Versions", out var versionsObj) && versionsObj is List<object> versions)
                                {
                                    foreach (var verObj in versions)
                                    {
                                        if (verObj is Dictionary<object, object> verDict && verDict.TryGetValue("Version", out var verNumObj))
                                        {
                                            var verNum = verNumObj?.ToString() ?? "1";
                                            var prefix = $"{langName}_{verNum}";
                                            ProcessFields(verDict, "Fields", prefix, itemId, properties, edges);
                                        }
                                    }
                                }
                            }
                        }
                    }

                    nodes.Add(new GraphNode
                    {
                        Id = itemId,
                        Name = itemName,
                        Type = "SitecoreItem",
                        FilePath = filePath,
                        Properties = properties
                    });
                }
            }
        }
        catch (Exception)
        {
            // Ignore YAML parsing errors
        }

        return Task.FromResult<(IReadOnlyList<GraphNode>, IReadOnlyList<GraphEdge>)>((nodes, edges));
    }

    private string GetItemName(Dictionary<string, object> doc, string filePath)
    {
        if (doc.TryGetValue(NameField, out var n) && n != null)
            return n.ToString()!;
            
        if (doc.TryGetValue(PathField, out var p) && p is string pathStr)
        {
            var segments = pathStr.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > 0)
                return segments.Last();
        }

        return Path.GetFileNameWithoutExtension(filePath);
    }

    private void ProcessFields(Dictionary<object, object> sourceDict, string key, string? prefix, string itemId, Dictionary<string, string> properties, List<GraphEdge> edges)
    {
        // For dictionaries where keys are objects (from YamlDotNet list parsing)
        if (sourceDict.TryGetValue(key, out var fieldsObj) && fieldsObj is List<object> fieldsList)
        {
            ParseFieldList(fieldsList, prefix, itemId, properties, edges);
        }
    }

    private void ProcessFields(Dictionary<string, object> sourceDict, string key, string? prefix, string itemId, Dictionary<string, string> properties, List<GraphEdge> edges)
    {
        // For root dictionary
        if (sourceDict.TryGetValue(key, out var fieldsObj) && fieldsObj is List<object> fieldsList)
        {
            ParseFieldList(fieldsList, prefix, itemId, properties, edges);
        }
    }

    private void ParseFieldList(List<object> fieldsList, string? prefix, string itemId, Dictionary<string, string> properties, List<GraphEdge> edges)
    {
        foreach (var fieldItem in fieldsList)
        {
            if (fieldItem is Dictionary<object, object> fieldDict)
            {
                var hint = fieldDict.TryGetValue("Hint", out var h) ? h?.ToString() : null;
                var val = fieldDict.TryGetValue("Value", out var v) ? v?.ToString() : null;

                if (!string.IsNullOrEmpty(hint) && val != null)
                {
                    var propKey = string.IsNullOrEmpty(prefix) ? $"Field_{hint}" : $"Field_{prefix}_{hint}";
                    
                    // Cap property length if it's too large to keep graph memory sane
                    properties[propKey] = val.Length > 1000 ? val.Substring(0, 1000) + "..." : val;

                    // Exclude standard fields (starting with __) from creating relationships
                    if (!hint.StartsWith("__"))
                    {
                        ExtractGuidRelationships(itemId, val, hint, edges);
                    }
                }
            }
        }
    }

    private void ExtractGuidRelationships(string sourceId, string value, string hint, List<GraphEdge> edges)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        var matches = GuidRegex.Matches(value);
        foreach (Match match in matches)
        {
            var targetId = match.Value;
            
            // Ignore self-references or empty matches
            if (!string.IsNullOrEmpty(targetId) && !targetId.Equals(sourceId, StringComparison.OrdinalIgnoreCase))
            {
                edges.Add(new GraphEdge
                {
                    SourceId = sourceId,
                    TargetId = targetId,
                    Relationship = "REFERENCES",
                    // Keep the hint as metadata so we know WHICH field references the target.
                    // (Previously assigned to the RelationType alias, which silently overwrote Relationship.)
                    Properties = new Dictionary<string, string> { ["field"] = hint }
                });
            }
        }
    }
}
