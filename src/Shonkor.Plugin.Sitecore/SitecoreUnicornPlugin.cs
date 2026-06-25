using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using YamlDotNet.Serialization;

namespace Shonkor.Plugin.Sitecore;

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
                var rawId = idObj as string;
                if (!string.IsNullOrEmpty(rawId))
                {
                    // Normalize the item id so edge targets (which arrive from layout XML / field
                    // values in {BRACED}/UPPERCASE form) resolve to the same canonical node id.
                    var itemId = NormalizeGuid(rawId);
                    var itemName = GetItemName(doc, filePath);

                    var properties = new Dictionary<string, string>
                    {
                        ["cms"] = "Sitecore Unicorn"
                    };

                    // Extract Relationships from Root
                    if (doc.TryGetValue(TemplateField, out var tmplObj) && tmplObj is string templateId && !string.IsNullOrEmpty(templateId))
                    {
                        var target = NormalizeGuid(templateId);
                        edges.Add(new GraphEdge { SourceId = itemId, TargetId = target, Relationship = "BASED_ON_TEMPLATE" });
                        properties["TemplateId"] = target;
                    }

                    if (doc.TryGetValue(ParentField, out var parentObj) && parentObj is string parentId && !string.IsNullOrEmpty(parentId))
                    {
                        var parent = NormalizeGuid(parentId);
                        edges.Add(new GraphEdge { SourceId = parent, TargetId = itemId, Relationship = "HAS_CHILD" });
                        properties["ParentId"] = parent;
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

                    // Cap stored property length to keep graph memory sane (extraction below uses the full value).
                    properties[propKey] = val.Length > 1000 ? val.Substring(0, 1000) + "..." : val;

                    // The most valuable Sitecore relationships live in standard (__) fields, so they are
                    // handled explicitly rather than excluded:
                    if (hint is "__Renderings" or "__Final Renderings")
                    {
                        // Presentation graph: which rendering sits on which placeholder with which datasource.
                        ExtractRenderings(itemId, val, hint, edges);
                    }
                    else if (hint == "__Base template")
                    {
                        // Template inheritance.
                        ExtractBaseTemplates(itemId, val, edges);
                    }
                    else if (!hint.StartsWith("__"))
                    {
                        // Other standard (__) fields stay excluded to avoid noise; non-standard fields
                        // get generic GUID-reference extraction.
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
            var targetId = NormalizeGuid(match.Value);

            // Ignore self-references or empty matches
            if (!string.IsNullOrEmpty(targetId) && !targetId.Equals(sourceId, StringComparison.OrdinalIgnoreCase))
            {
                edges.Add(new GraphEdge
                {
                    SourceId = sourceId,
                    TargetId = targetId,
                    Relationship = "REFERENCES",
                    // Keep the hint as metadata so we know WHICH field references the target.
                    Properties = new Dictionary<string, string> { ["field"] = hint }
                });
            }
        }
    }

    /// <summary>
    /// F1 — Presentation graph. Parses the Sitecore layout XML stored in __Renderings /
    /// __Final Renderings and emits, per rendering instance:
    ///   item --HAS_RENDERING(placeholder,uid)--> renderingDefinitionItem
    ///   item --USES_DATASOURCE(rendering,placeholder)--> datasourceItem
    /// </summary>
    private void ExtractRenderings(string itemId, string layoutValue, string hint, List<GraphEdge> edges)
    {
        if (string.IsNullOrWhiteSpace(layoutValue) || !layoutValue.TrimStart().StartsWith("<"))
            return;

        try
        {
            var xml = XDocument.Parse(layoutValue);

            // Rendering instances are <r> elements carrying an 's' (rendering definition id) attribute,
            // nested under device <d> elements. The layout root is also <r> but has no 's'.
            foreach (var r in xml.Descendants("r"))
            {
                var renderingRef = (string?)r.Attribute("s") ?? (string?)r.Attribute("id");
                if (string.IsNullOrEmpty(renderingRef)) continue;

                var renderingMatch = GuidRegex.Match(renderingRef);
                if (!renderingMatch.Success) continue;
                var renderingId = NormalizeGuid(renderingMatch.Value);

                var placeholder = (string?)r.Attribute("ph") ?? (string?)r.Attribute("p") ?? string.Empty;
                var uid = (string?)r.Attribute("uid") ?? string.Empty;
                var dsRaw = (string?)r.Attribute("ds") ?? string.Empty;

                edges.Add(new GraphEdge
                {
                    SourceId = itemId,
                    TargetId = renderingId,
                    Relationship = "HAS_RENDERING",
                    Properties = new Dictionary<string, string>
                    {
                        ["field"] = hint,
                        ["placeholder"] = placeholder,
                        ["uid"] = uid
                    }
                });

                var dsMatch = GuidRegex.Match(dsRaw);
                if (dsMatch.Success)
                {
                    var datasourceId = NormalizeGuid(dsMatch.Value);
                    if (!datasourceId.Equals(itemId, StringComparison.OrdinalIgnoreCase))
                    {
                        edges.Add(new GraphEdge
                        {
                            SourceId = itemId,
                            TargetId = datasourceId,
                            Relationship = "USES_DATASOURCE",
                            Properties = new Dictionary<string, string>
                            {
                                ["rendering"] = renderingId,
                                ["placeholder"] = placeholder
                            }
                        });
                    }
                }
                // Non-GUID datasources (query: / path /sitecore/...) cannot be linked by id without a
                // graph-aware second pass; intentionally skipped here (see F3/F8 in the gap analysis).
            }
        }
        catch (Exception)
        {
            // Malformed layout XML — skip presentation extraction for this field; keep the item and
            // its other edges. (A future diagnostics pass should surface this instead of swallowing it.)
        }
    }

    /// <summary>
    /// F2 — Template inheritance. The __Base template field holds a pipe-separated list of base
    /// template GUIDs; emits item --INHERITS_FROM--> baseTemplate edges.
    /// </summary>
    private void ExtractBaseTemplates(string itemId, string value, List<GraphEdge> edges)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        foreach (Match match in GuidRegex.Matches(value))
        {
            var baseId = NormalizeGuid(match.Value);
            if (!string.IsNullOrEmpty(baseId) && !baseId.Equals(itemId, StringComparison.OrdinalIgnoreCase))
            {
                edges.Add(new GraphEdge
                {
                    SourceId = itemId,
                    TargetId = baseId,
                    Relationship = "INHERITS_FROM",
                    Properties = new Dictionary<string, string> { ["field"] = "__Base template" }
                });
            }
        }
    }

    /// <summary>
    /// Canonicalises a Sitecore GUID to lowercase, dashed, brace-less form so that ids coming from
    /// item ID fields (lowercase/dashed) and from layout XML / field values ({BRACED}/UPPERCASE)
    /// reference the same graph node.
    /// </summary>
    private static string NormalizeGuid(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw ?? string.Empty;
        var m = GuidRegex.Match(raw);
        if (!m.Success) return raw;
        return Guid.TryParse(m.Value, out var g) ? g.ToString("D") : m.Value.ToLowerInvariant();
    }
}
