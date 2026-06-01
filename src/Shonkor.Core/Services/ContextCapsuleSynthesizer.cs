// Licensed to Shonkor under the MIT License.

using System.Text;
using Shonkor.Core.Models;

namespace Shonkor.Core.Services;

/// <summary>
/// Synthesizes a token-optimized, prompt-friendly context capsule in Markdown
/// from a retrieved subgraph of nodes and edges, including an inline Mermaid.js diagram.
/// </summary>
public sealed class ContextCapsuleSynthesizer
{
    /// <summary>
    /// Synthesizes a Markdown-formatted context capsule representing the given subgraph.
    /// </summary>
    /// <param name="nodes">The collection of nodes in the subgraph.</param>
    /// <param name="edges">The collection of edges linking nodes in the subgraph.</param>
    /// <returns>A Markdown string containing the structured context and inline diagrams.</returns>
    public string Synthesize(IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);

        var sb = new StringBuilder();

        sb.AppendLine("# Shonkor Context Capsule");
        sb.AppendLine();
        sb.AppendLine("> [!NOTE]");
        sb.AppendLine("> This context capsule was synthesized automatically by Shonkor.");
        sb.AppendLine("> It contains a precise structural subgraph of the codebase relevant to your query.");
        sb.AppendLine();

        // 1. Render Summary Statistics
        sb.AppendLine("## Subgraph Summary");
        sb.AppendLine($"- **Total Nodes:** {nodes.Count}");
        sb.AppendLine($"- **Total Edges:** {edges.Count}");
        sb.AppendLine();

        // Group by node type
        var nodesByType = nodes.GroupBy(n => n.Type).ToDictionary(g => g.Key, g => g.Count());
        sb.Append("- **Node Composition:** ");
        sb.AppendLine(string.Join(", ", nodesByType.Select(kvp => $"{kvp.Key}: {kvp.Value}")));
        sb.AppendLine();

        // 2. Render Mermaid.js Architectural Diagram
        if (nodes.Count > 0)
        {
            sb.AppendLine("## Structural Architecture (Mermaid.js)");
            sb.AppendLine("```mermaid");
            sb.AppendLine("graph TD");

            // Define node styling classes
            sb.AppendLine("    classDef file fill:#1d3557,stroke:#457b9d,stroke-width:2px,color:#fff;");
            sb.AppendLine("    classDef classNode fill:#2a9d8f,stroke:#264653,stroke-width:2px,color:#fff;");
            sb.AppendLine("    classDef methodNode fill:#9b5de5,stroke:#5c3d75,stroke-width:2px,color:#fff;");
            sb.AppendLine("    classDef decision fill:#e76f51,stroke:#d62828,stroke-width:2px,color:#fff;");
            sb.AppendLine("    classDef milestone fill:#f4a261,stroke:#e76f51,stroke-width:2px,color:#fff;");
            sb.AppendLine("    classDef defaultNode fill:#6c757d,stroke:#495057,stroke-width:2px,color:#fff;");
            sb.AppendLine();

            // Store safe IDs for Mermaid (Mermaid doesn't like paths or special chars as identifiers)
            var mermaidIdMap = new Dictionary<string, string>();
            var index = 0;
            foreach (var node in nodes)
            {
                var safeId = $"n{index++}";
                mermaidIdMap[node.Id] = safeId;

                // Escape double quotes inside the name
                var cleanName = node.Name.Replace("\"", "\\\"");
                var label = $"{node.Type}: {cleanName}";

                sb.AppendLine($"    {safeId}[\"{label}\"]");

                // Assign styles based on Type
                var styleClass = node.Type.ToLowerInvariant() switch
                {
                    "file" => "file",
                    "class" or "interface" or "record" => "classNode",
                    "method" => "methodNode",
                    "decision" => "decision",
                    "milestone" => "milestone",
                    _ => "defaultNode"
                };
                sb.AppendLine($"    class {safeId} {styleClass};");
            }

            sb.AppendLine();

            // Render Edges
            var renderedEdges = new HashSet<(string, string, string)>();
            foreach (var edge in edges)
            {
                if (mermaidIdMap.TryGetValue(edge.SourceId, out var srcSafe) &&
                    mermaidIdMap.TryGetValue(edge.TargetId, out var tgtSafe))
                {
                    var key = (srcSafe, tgtSafe, edge.Relationship);
                    if (renderedEdges.Add(key))
                    {
                        var relLabel = string.IsNullOrEmpty(edge.Relationship) ? string.Empty : $"|{edge.Relationship}|";
                        sb.AppendLine($"    {srcSafe} -->{relLabel} {tgtSafe}");
                    }
                }
            }

            sb.AppendLine("```");
            sb.AppendLine();
        }

        // 3. Render Node Contents Grouped by File
        sb.AppendLine("## Code & Content References");
        sb.AppendLine();

        var nodesByFile = nodes.GroupBy(n => n.FilePath).OrderBy(g => g.Key);

        foreach (var fileGroup in nodesByFile)
        {
            var filePath = string.IsNullOrEmpty(fileGroup.Key) ? "Virtual / External Nodes" : fileGroup.Key;
            sb.AppendLine($"### 📄 {Path.GetFileName(filePath)}");
            sb.AppendLine($"- **Full Path:** `{filePath}`");
            sb.AppendLine();

            var sortedNodes = fileGroup.OrderBy(n => n.StartLine ?? 0).ThenBy(n => n.Type);

            foreach (var node in sortedNodes)
            {
                // Skip file nodes since we want to focus on their child AST definitions unless it has markdown/text content
                if (node.Type.Equals("file", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(node.Content))
                {
                    continue;
                }

                var lineInfo = node.StartLine.HasValue && node.EndLine.HasValue
                    ? $" (Lines {node.StartLine}-{node.EndLine})"
                    : string.Empty;

                sb.AppendLine($"#### 🏷️ `{node.Type}`: **{node.Name}**{lineInfo}");
                
                if (node.Properties.Count > 0)
                {
                    sb.AppendLine("**Properties:**");
                    foreach (var kvp in node.Properties)
                    {
                        if (kvp.Key == "Content" || kvp.Key == "FilePath" || kvp.Key == "StartLine" || kvp.Key == "EndLine" || kvp.Key == "ContentHash")
                        {
                            continue; // Skip standard structural properties as they're rendered cleanly elsewhere
                        }
                        sb.AppendLine($"- `{kvp.Key}`: {kvp.Value}");
                    }
                }
                
                sb.AppendLine();

                if (!string.IsNullOrWhiteSpace(node.Content))
                {
                    var syntaxHighlight = GetSyntaxHighlightLanguage(filePath);
                    sb.AppendLine($"```{syntaxHighlight}");
                    sb.AppendLine(node.Content.TrimEnd());
                    sb.AppendLine("```");
                    sb.AppendLine();
                }
            }

            sb.AppendLine("---");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string GetSyntaxHighlightLanguage(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".cs" => "csharp",
            ".js" or ".jsx" => "javascript",
            ".ts" or ".tsx" => "typescript",
            ".php" => "php",
            ".yml" or ".yaml" => "yaml",
            ".md" => "markdown",
            ".json" => "json",
            ".html" => "html",
            ".css" => "css",
            _ => "text"
        };
    }
}
