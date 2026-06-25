using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;

namespace Shonkor.Plugin.Sitecore;

/// <summary>
/// Sitecore XM Cloud / JSS (headless) parser.
///
/// JSS components (.tsx/.jsx) become <c>XmCloudComponent</c> nodes keyed by a stable name-based id
/// (<c>xmcloud:component:{name}</c>), enriched with the Sitecore fields and placeholders they use.
/// Layout-Service route data (.json) becomes <c>XmCloudRouteData</c> and is walked into the placeholder
/// tree, emitting:
///   route --RENDERS_COMPONENT(placeholder)--> xmcloud:component:{name}
///   route --USES_DATASOURCE(component,placeholder)--> datasourceItem   (when the datasource is a GUID)
/// The name-based component id lets a route's componentName resolve to the .tsx that defines it, and the
/// normalised datasource GUID links to the Unicorn-serialised item — across files, without a second pass.
/// </summary>
public sealed class SitecoreXmCloudPlugin : IFileParser
{
    private static readonly Regex GuidRegex = new(@"[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}", RegexOptions.Compiled);
    private static readonly Regex FieldDotRegex = new(@"\bfields\.([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
    private static readonly Regex FieldIndexRegex = new(@"\bfields\[\s*[""']([^""']+)[""']\s*\]", RegexOptions.Compiled);
    private static readonly Regex PlaceholderRegex = new(@"<Placeholder\b[^>]*?\bname\s*=\s*[""']([^""']+)[""']", RegexOptions.Compiled);

    public IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".tsx", ".jsx", ".ts", ".js", ".json" }.ToFrozenSet();

    public IReadOnlyList<NodeTypeDescriptor> NodeTypeDescriptors { get; } = new[]
    {
        new NodeTypeDescriptor("XmCloudComponent", "CMS", true),
        new NodeTypeDescriptor("XmCloudRouteData", "CMS", true)
    };

    public Task<(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)> ParseAsync(string filePath, string content)
    {
        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();

        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        if (ext is ".tsx" or ".jsx" or ".ts" or ".js")
        {
            ParseJssComponent(filePath, content, nodes, edges);
        }
        else if (ext == ".json")
        {
            ParseRouteJson(filePath, content, nodes, edges);
        }

        return Task.FromResult<(IReadOnlyList<GraphNode>, IReadOnlyList<GraphEdge>)>((nodes, edges));
    }

    private void ParseJssComponent(string filePath, string content, List<GraphNode> nodes, List<GraphEdge> edges)
    {
        // Heuristic JSS detection: the package import or the (legacy) context HOC/hook.
        var isJss = content.Contains("@sitecore-jss")
                    || content.Contains("withSitecoreContext")
                    || content.Contains("useSitecoreContext");
        if (!isJss) return;

        var componentName = ComponentNameFromPath(filePath);
        var componentId = ComponentId(componentName);

        var fields = FieldDotRegex.Matches(content).Select(m => m.Groups[1].Value)
            .Concat(FieldIndexRegex.Matches(content).Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        var placeholders = PlaceholderRegex.Matches(content).Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        var properties = new Dictionary<string, string>
        {
            ["cms"] = "Sitecore XM Cloud",
            ["componentName"] = componentName
        };
        if (fields.Count > 0) properties["fieldsUsed"] = string.Join(",", fields);
        if (placeholders.Count > 0) properties["placeholdersExposed"] = string.Join(",", placeholders);

        nodes.Add(new GraphNode
        {
            Id = componentId,
            Name = componentName,
            Type = "XmCloudComponent",
            FilePath = filePath,
            Properties = properties
        });

        // Link the source file to the component definition so a route -> component edge chains back to
        // the implementing file: file --DEFINES_COMPONENT--> component <--RENDERS_COMPONENT-- route.
        edges.Add(new GraphEdge
        {
            SourceId = filePath,
            TargetId = componentId,
            Relationship = "DEFINES_COMPONENT"
        });
    }

    private void ParseRouteJson(string filePath, string content, List<GraphNode> nodes, List<GraphEdge> edges)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(content); }
        catch { return; } // not JSON we can read; ignore (e.g. JSON5 / malformed)

        using (doc)
        {
            if (!TryGetRoute(doc.RootElement, out var route)) return;

            var routeId = filePath;
            var properties = new Dictionary<string, string> { ["cms"] = "Sitecore XM Cloud" };
            if (route.ValueKind == JsonValueKind.Object && route.TryGetProperty("name", out var rn) && rn.ValueKind == JsonValueKind.String)
                properties["routeName"] = rn.GetString()!;

            nodes.Add(new GraphNode
            {
                Id = routeId,
                Name = Path.GetFileName(filePath),
                Type = "XmCloudRouteData",
                FilePath = filePath,
                Properties = properties
            });

            WalkPlaceholders(routeId, route, edges);
        }
    }

    private static bool TryGetRoute(JsonElement root, out JsonElement route)
    {
        route = default;
        if (root.ValueKind != JsonValueKind.Object) return false;

        // Layout Service: { sitecore: { route: {...} } }
        if (root.TryGetProperty("sitecore", out var sc) && sc.ValueKind == JsonValueKind.Object
            && sc.TryGetProperty("route", out var r) && r.ValueKind == JsonValueKind.Object)
        {
            route = r;
            return true;
        }
        // { route: {...} }
        if (root.TryGetProperty("route", out var r2) && r2.ValueKind == JsonValueKind.Object)
        {
            route = r2;
            return true;
        }
        // Disconnected route data: the object itself carries placeholders/componentName.
        if (root.TryGetProperty("placeholders", out _) || root.TryGetProperty("componentName", out _))
        {
            route = root;
            return true;
        }
        return false;
    }

    private void WalkPlaceholders(string routeId, JsonElement element, List<GraphEdge> edges)
    {
        if (element.ValueKind != JsonValueKind.Object) return;
        if (!element.TryGetProperty("placeholders", out var placeholders) || placeholders.ValueKind != JsonValueKind.Object)
            return;

        foreach (var ph in placeholders.EnumerateObject())
        {
            var placeholderName = ph.Name;
            if (ph.Value.ValueKind != JsonValueKind.Array) continue;

            foreach (var component in ph.Value.EnumerateArray())
            {
                if (component.ValueKind != JsonValueKind.Object) continue;

                var componentName = GetString(component, "componentName");
                if (!string.IsNullOrEmpty(componentName))
                {
                    edges.Add(new GraphEdge
                    {
                        SourceId = routeId,
                        TargetId = ComponentId(componentName),
                        Relationship = "RENDERS_COMPONENT",
                        Properties = new Dictionary<string, string> { ["placeholder"] = placeholderName }
                    });
                }

                var dataSource = GetString(component, "dataSource") ?? GetString(component, "datasource");
                if (!string.IsNullOrEmpty(dataSource))
                {
                    var dsMatch = GuidRegex.Match(dataSource);
                    if (dsMatch.Success)
                    {
                        edges.Add(new GraphEdge
                        {
                            SourceId = routeId,
                            TargetId = NormalizeGuid(dsMatch.Value),
                            Relationship = "USES_DATASOURCE",
                            Properties = new Dictionary<string, string>
                            {
                                ["component"] = componentName ?? string.Empty,
                                ["placeholder"] = placeholderName
                            }
                        });
                    }
                }

                // Nested placeholders inside this component instance.
                WalkPlaceholders(routeId, component, edges);
            }
        }
    }

    private static string? GetString(JsonElement obj, string property)
        => obj.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string ComponentNameFromPath(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        // JSS components are often folder/index.tsx; use the folder name in that case.
        if (string.Equals(name, "index", StringComparison.OrdinalIgnoreCase))
        {
            var dir = Path.GetFileName(Path.GetDirectoryName(filePath) ?? string.Empty);
            if (!string.IsNullOrEmpty(dir)) name = dir;
        }
        return name;
    }

    private static string ComponentId(string componentName) => $"xmcloud:component:{componentName.ToLowerInvariant()}";

    private static string NormalizeGuid(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw ?? string.Empty;
        var m = GuidRegex.Match(raw);
        if (!m.Success) return raw;
        return Guid.TryParse(m.Value, out var g) ? g.ToString("D") : m.Value.ToLowerInvariant();
    }
}
