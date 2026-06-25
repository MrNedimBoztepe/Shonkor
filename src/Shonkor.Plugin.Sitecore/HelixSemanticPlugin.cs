using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;

namespace Shonkor.Plugin.Sitecore;

/// <summary>
/// Extracts Sitecore Helix architectural concepts (Feature / Foundation / Project) from file paths
/// during indexing and links each file to the concept it belongs to. This yields instant architectural
/// structure (and a basis for Helix dependency-rule checks) without any AI/LLM analysis.
/// </summary>
public sealed class HelixSemanticPlugin : IFileParser
{
    public IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".yml", ".yaml", ".json", ".xml", ".config", ".js", ".ts", ".jsx", ".tsx", ".html", ".cshtml"
        }.ToFrozenSet();

    public IReadOnlyList<NodeTypeDescriptor> NodeTypeDescriptors { get; } = new[]
    {
        new NodeTypeDescriptor("Concept", "CMS", true)
    };

    // Matches Helix paths like  src/Feature/Checkout/code/...  or  src\Foundation\Serialization\...
    private static readonly Regex HelixPattern = new Regex(
        @"[/\\](?<layer>Feature|Foundation|Project)[/\\](?<module>[^/\\]+)[/\\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public Task<(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)> ParseAsync(string filePath, string content)
    {
        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();

        var match = HelixPattern.Match(filePath);
        if (match.Success)
        {
            // Canonicalise layer casing so "feature" and "Feature" map to the same concept id.
            var rawLayer = match.Groups["layer"].Value;
            var layer = char.ToUpperInvariant(rawLayer[0]) + rawLayer.Substring(1).ToLowerInvariant();
            var module = match.Groups["module"].Value;
            var conceptId = $"Concept:{layer}:{module}";

            nodes.Add(new GraphNode
            {
                Id = conceptId,
                Type = "Concept",
                Name = $"{module} ({layer})",
                Content = $"Sitecore Helix concept: {module} in the {layer} layer.",
                FilePath = filePath,
                Properties = new Dictionary<string, string>
                {
                    ["HelixLayer"] = layer,
                    ["HelixModule"] = module
                }
            });

            // The file node is created by the indexer keyed on its path; link it to the concept.
            edges.Add(new GraphEdge
            {
                SourceId = filePath,
                TargetId = conceptId,
                Relationship = "BELONGS_TO_CONCEPT"
            });
        }

        return Task.FromResult<(IReadOnlyList<GraphNode>, IReadOnlyList<GraphEdge>)>((nodes, edges));
    }
}
