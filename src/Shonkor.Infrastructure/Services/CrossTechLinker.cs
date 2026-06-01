using System.Text.RegularExpressions;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;

namespace Shonkor.Infrastructure.Services;

/// <summary>
/// Performs a post-scan connection pass across the knowledge graph database.
/// Dynamically links frontend JSComponents, GraphQLQueries, C# AST classes,
/// and Sitecore SCS item nodes via name-matching, configuration values, and Helix architecture patterns.
/// </summary>
public static class CrossTechLinker
{
    /// <summary>
    /// Executes the post-processing phase, mapping cross-technology edges and Helix architecture modules.
    /// </summary>
    public static async Task EstablishCrossTechnologyConnectionsAsync(
        IGraphStorageProvider storage,
        string directoryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);

        // 1. Fetch all nodes in the database to do in-memory analysis
        var allNodes = await storage.GetAllNodesAsync(cancellationToken).ConfigureAwait(false);
        if (allNodes.Count == 0) return;

        var jsComponents = allNodes.Where(n => n.Type == "JSComponent").ToList();
        var sitecoreRenderings = allNodes.Where(n => n.Type == "SitecoreRendering").ToList();
        var sitecoreTemplates = allNodes.Where(n => n.Type == "SitecoreTemplate").ToList();
        var sitecoreItems = allNodes.Where(n => n.Type == "SitecoreItem" || n.Type == "SitecoreTemplate" || n.Type == "SitecoreRendering").ToList();
        var csharpClasses = allNodes.Where(n => n.Type == "Class" || n.Type == "Record" || n.Type == "Interface").ToList();
        var graphqlNodes = allNodes.Where(n => n.Type == "GraphQLQuery" || n.Type == "GraphQLFragment").ToList();

        var newEdges = new List<GraphEdge>();
        var virtualNodes = new List<GraphNode>();
        var createdModuleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Helper to normalize names for fuzzy matching (removes spaces, casing, and common suffixes)
        string NormalizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var clean = name.Trim().ToLowerInvariant();
            clean = clean.Replace("controller", "");
            return clean;
        }

        // 2. Map Next.js JSComponents to Sitecore Renderings/Templates
        foreach (var jsComp in jsComponents)
        {
            var jsNorm = NormalizeName(jsComp.Name);
            if (string.IsNullOrEmpty(jsNorm)) continue;

            // Name-based fuzzy mapping: if JSComponent name matches Sitecore template or rendering name
            foreach (var scItem in sitecoreItems)
            {
                var scNorm = NormalizeName(scItem.Name);
                if (scNorm == jsNorm)
                {
                    newEdges.Add(new GraphEdge
                    {
                        SourceId = jsComp.Id,
                        TargetId = scItem.Id,
                        Relationship = "BINDS_TO",
                        Properties = new Dictionary<string, string>
                        {
                            ["MappingType"] = "ImplicitNameMatch"
                        }
                    });
                }
            }
        }

        // 3. Map Explicit Sitecore Bindings (componentName, controller, viewPath)
        foreach (var scItem in sitecoreItems)
        {
            // A. Component Name headless mapping
            if (scItem.Properties.TryGetValue("componentName", out var compName) && !string.IsNullOrWhiteSpace(compName))
            {
                var normComp = NormalizeName(compName);
                var match = jsComponents.FirstOrDefault(js => NormalizeName(js.Name) == normComp);
                if (match != null)
                {
                    newEdges.Add(new GraphEdge
                    {
                        SourceId = match.Id,
                        TargetId = scItem.Id,
                        Relationship = "BINDS_TO",
                        Properties = new Dictionary<string, string>
                        {
                            ["MappingType"] = "ExplicitComponentName"
                        }
                    });
                }
            }

            // B. Controller mapping
            if (scItem.Properties.TryGetValue("controller", out var controllerStr) && !string.IsNullOrWhiteSpace(controllerStr))
            {
                // Extract controller class name (e.g., "MuM.Feature.Blog.Controllers.BlogController, MuM.Feature.Blog" -> "BlogController")
                var controllerClass = controllerStr.Split(',')[0].Split('.').Last();
                var match = csharpClasses.FirstOrDefault(c => string.Equals(c.Name, controllerClass, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    newEdges.Add(new GraphEdge
                    {
                        SourceId = match.Id,
                        TargetId = scItem.Id,
                        Relationship = "CONTROLLER_OF",
                        Properties = new Dictionary<string, string>
                        {
                            ["MappingType"] = "ExplicitControllerName",
                            ["ControllerAction"] = scItem.Properties.TryGetValue("controllerAction", out var act) ? act : ""
                        }
                    });
                }
            }
        }

        // 4. Map GraphQL Queries to Sitecore Templates they request (e.g., ... on Promo)
        foreach (var gqlNode in graphqlNodes)
        {
            if (gqlNode.Properties.TryGetValue("referencedTemplates", out var refTemplatesStr) && !string.IsNullOrWhiteSpace(refTemplatesStr))
            {
                var templates = refTemplatesStr.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var tempName in templates)
                {
                    var normTemp = NormalizeName(tempName);
                    var match = sitecoreTemplates.FirstOrDefault(t => NormalizeName(t.Name) == normTemp);
                    if (match != null)
                    {
                        newEdges.Add(new GraphEdge
                        {
                            SourceId = gqlNode.Id,
                            TargetId = match.Id,
                            Relationship = "QUERIES_TEMPLATE",
                            Properties = new Dictionary<string, string>
                            {
                                ["MappingType"] = "InlineFragmentReference"
                            }
                        });
                    }
                }
            }
        }

        // 4.5 Resolve C# type references (referencedTypes property) into REFERENCES_TYPE edges.
        // This turns the parser's bare type-name references into real node-to-node dependency edges,
        // enabling "who uses type X?" impact traversal across files.
        var typeDefinitionsByName = new Dictionary<string, List<GraphNode>>(StringComparer.Ordinal);
        foreach (var def in allNodes.Where(n => n.Type is "Class" or "Interface" or "Record" or "Struct" or "Enum"))
        {
            if (string.IsNullOrEmpty(def.Name)) continue;
            if (!typeDefinitionsByName.TryGetValue(def.Name, out var list))
            {
                list = new List<GraphNode>();
                typeDefinitionsByName[def.Name] = list;
            }
            list.Add(def);
        }

        foreach (var node in allNodes)
        {
            if (!node.Properties.TryGetValue("referencedTypes", out var referencedCsv) || string.IsNullOrWhiteSpace(referencedCsv))
            {
                continue;
            }

            foreach (var typeName in referencedCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!typeDefinitionsByName.TryGetValue(typeName, out var definitions))
                {
                    continue; // Reference to a type not defined in this codebase (e.g. BCL) -> no edge.
                }

                foreach (var definition in definitions)
                {
                    if (definition.Id == node.Id) continue; // skip self

                    newEdges.Add(new GraphEdge
                    {
                        SourceId = node.Id,
                        TargetId = definition.Id,
                        Relationship = "REFERENCES_TYPE",
                        Properties = new Dictionary<string, string>
                        {
                            ["MappingType"] = "ResolvedTypeReference"
                        }
                    });
                }
            }
        }

        // 5. Build Helix Architecture Module mapping (Feature / Foundation / Project layers)
        foreach (var node in allNodes)
        {
            var filePath = node.Properties.TryGetValue("filePath", out var fp) ? fp : (node.Properties.TryGetValue("FilePath", out var fp2) ? fp2 : null);
            var sitecorePath = node.Properties.TryGetValue("sitecorePath", out var sp) ? sp : null;

            var helixInfo = GetHelixModule(filePath, sitecorePath);
            if (helixInfo != null)
            {
                var moduleId = $"helix::{helixInfo.Value.Layer.ToLowerInvariant()}::{helixInfo.Value.Module.ToLowerInvariant()}";
                
                // Add the virtual HelixModule node if not already added to current batch
                if (!createdModuleIds.Contains(moduleId))
                {
                    createdModuleIds.Add(moduleId);
                    virtualNodes.Add(new GraphNode
                    {
                        Id = moduleId,
                        Name = $"{helixInfo.Value.Layer}.{helixInfo.Value.Module}",
                        Type = "HelixModule",
                        Properties = new Dictionary<string, string>
                        {
                            ["layer"] = helixInfo.Value.Layer,
                            ["module"] = helixInfo.Value.Module
                        }
                    });
                }

                // Connect current node to its modular Helix group
                newEdges.Add(new GraphEdge
                {
                    SourceId = node.Id,
                    TargetId = moduleId,
                    Relationship = "BELONGS_TO_MODULE"
                });
            }
        }

        // 6. Persist newly identified elements to database
        if (virtualNodes.Count > 0)
        {
            await storage.UpsertNodesAsync(virtualNodes, cancellationToken).ConfigureAwait(false);
        }

        if (newEdges.Count > 0)
        {
            await storage.UpsertEdgesAsync(newEdges, cancellationToken).ConfigureAwait(false);
        }
    }

    private record struct HelixModuleInfo(string Layer, string Module);

    /// <summary>
    /// Parsen des Helix Layer und Moduls aus Datei- oder Sitecore-Pfade.
    /// </summary>
    private static HelixModuleInfo? GetHelixModule(string? filePath, string? sitecorePath)
    {
        // 1. Check Sitecore paths (e.g. /sitecore/templates/Feature/Blog/BlogBox)
        if (!string.IsNullOrWhiteSpace(sitecorePath))
        {
            var parts = sitecorePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length - 1; i++)
            {
                var part = parts[i];
                if (string.Equals(part, "Feature", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(part, "Foundation", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(part, "Project", StringComparison.OrdinalIgnoreCase))
                {
                    return new HelixModuleInfo(Capitalize(part), Capitalize(parts[i + 1]));
                }
            }
        }

        // 2. Check physical file paths (e.g. C:\Projects\sitecoreMuM\src\Feature\Blog\code\Controllers\BlogController.cs)
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var normalizedPath = filePath.Replace('\\', '/');
            var segments = normalizedPath.Split('/');
            for (int i = 0; i < segments.Length - 1; i++)
            {
                var segment = segments[i];
                if (string.Equals(segment, "Feature", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(segment, "Foundation", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(segment, "Project", StringComparison.OrdinalIgnoreCase))
                {
                    return new HelixModuleInfo(Capitalize(segment), Capitalize(segments[i + 1]));
                }
            }
        }

        return null;
    }

    private static string Capitalize(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();
    }
}
