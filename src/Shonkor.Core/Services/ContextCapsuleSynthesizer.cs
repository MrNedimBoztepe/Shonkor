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
        => Synthesize(nodes, edges, CapsuleOptions.Unlimited);

    /// <summary>
    /// Budget- and relevance-aware capsule synthesis (TICKET-003). Seeds are rendered first and in full;
    /// remaining nodes are ordered by incident-edge degree and rendered in full until the content budget
    /// (<see cref="CapsuleOptions.MaxContentChars"/>) is exhausted, after which only their signature/header
    /// is kept. Prevents 2-hop hub expansions from exploding the prompt while keeping the most relevant
    /// code at full fidelity. With <see cref="CapsuleOptions.Unlimited"/> the behaviour is the legacy
    /// file-grouped, full-content rendering (back-compat).
    /// </summary>
    public string Synthesize(IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges, CapsuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(options);

        // Hub protection (TICKET-003): a 2-hop expansion off a hub can pull in hundreds of nodes whose
        // per-node headers + diagram dominate the capsule regardless of the body budget. When MaxNodes is
        // set, keep only the most relevant nodes (seeds first, then by incident-edge degree) and restrict
        // the diagram/edges to that subset. Null = no cap (legacy behaviour).
        int trimmedAway = 0;
        if (options.MaxNodes is int cap && nodes.Count > cap)
        {
            var seedSet = options.SeedIds is null ? new HashSet<string>() : new HashSet<string>(options.SeedIds, StringComparer.Ordinal);
            var deg = new Dictionary<string, int>();
            foreach (var e in edges)
            {
                deg[e.SourceId] = deg.GetValueOrDefault(e.SourceId) + 1;
                deg[e.TargetId] = deg.GetValueOrDefault(e.TargetId) + 1;
            }
            trimmedAway = nodes.Count - cap;
            var kept = nodes
                .OrderByDescending(n => seedSet.Contains(n.Id))
                .ThenByDescending(n => deg.GetValueOrDefault(n.Id))
                .ThenBy(n => n.FilePath, StringComparer.Ordinal)
                .Take(cap)
                .ToList();
            var keptIds = new HashSet<string>(kept.Select(n => n.Id), StringComparer.Ordinal);
            nodes = kept;
            edges = edges.Where(e => keptIds.Contains(e.SourceId) && keptIds.Contains(e.TargetId)).ToList();
        }

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

        // 3. Render Node Contents.
        sb.AppendLine("## Code & Content References");
        sb.AppendLine();

        if (options.MaxContentChars is int budget)
        {
            RenderBudgeted(sb, nodes, edges, options, budget);
            if (trimmedAway > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"> {trimmedAway} more node(s) in the neighbourhood were omitted (lowest relevance) to bound the capsule. Expand with `get_subgraph` / `references` on demand.");
            }
            return sb.ToString();
        }

        // Legacy (unlimited) rendering: grouped by file, every body in full.
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

    /// <summary>
    /// Renders nodes in relevance order under a content budget: seeds first (full), then by incident-edge
    /// degree. Full bodies are emitted until the budget is spent; thereafter only a header + signature is
    /// kept and the body is noted as omitted. Seeds are always rendered in full and flagged as primary.
    /// </summary>
    private static void RenderBudgeted(
        StringBuilder sb,
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<GraphEdge> edges,
        CapsuleOptions options,
        int budget)
    {
        var seedIds = options.SeedIds is null
            ? new HashSet<string>()
            : new HashSet<string>(options.SeedIds, StringComparer.Ordinal);

        // Incident-edge degree per node — a cheap proxy for structural centrality.
        var degree = new Dictionary<string, int>();
        foreach (var e in edges)
        {
            degree[e.SourceId] = degree.GetValueOrDefault(e.SourceId) + 1;
            degree[e.TargetId] = degree.GetValueOrDefault(e.TargetId) + 1;
        }

        var ordered = nodes
            .Where(n => !(n.Type.Equals("file", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(n.Content)))
            .OrderByDescending(n => seedIds.Contains(n.Id))
            .ThenByDescending(n => degree.GetValueOrDefault(n.Id))
            .ThenBy(n => n.FilePath, StringComparer.Ordinal)
            .ThenBy(n => n.StartLine ?? 0)
            .ToList();

        var remaining = budget;
        var omitted = 0;

        foreach (var node in ordered)
        {
            var isSeed = seedIds.Contains(node.Id);
            var lineInfo = node.StartLine.HasValue && node.EndLine.HasValue
                ? $" (Lines {node.StartLine}-{node.EndLine})"
                : string.Empty;
            var badge = isSeed ? " — 🎯 primary" : string.Empty;
            var fileName = string.IsNullOrEmpty(node.FilePath) ? "Virtual / External" : Path.GetFileName(node.FilePath);

            sb.AppendLine($"#### 🏷️ `{node.Type}`: **{node.Name}**{lineInfo} · `{fileName}`{badge}");

            if (node.Properties.TryGetValue("signature", out var sig) && !string.IsNullOrWhiteSpace(sig))
            {
                sb.AppendLine($"`{sig}`");
            }
            if (!string.IsNullOrWhiteSpace(node.Summary))
            {
                sb.AppendLine($"> {node.Summary}");
            }
            sb.AppendLine();

            var content = node.Content?.TrimEnd() ?? string.Empty;
            if (content.Length == 0)
            {
                continue;
            }

            // Seeds are always emitted in full; non-seeds only while budget remains.
            if (isSeed || content.Length <= remaining)
            {
                var lang = GetSyntaxHighlightLanguage(node.FilePath ?? string.Empty);
                sb.AppendLine($"```{lang}");
                sb.AppendLine(content);
                sb.AppendLine("```");
                sb.AppendLine();
                if (!isSeed) remaining -= content.Length;
            }
            else
            {
                omitted++;
                sb.AppendLine($"_(body omitted — context budget reached; {content.Length} chars. Fetch on demand via `get_source`.)_");
                sb.AppendLine();
            }
        }

        if (omitted > 0)
        {
            sb.AppendLine("---");
            sb.AppendLine($"> [!NOTE]");
            sb.AppendLine($"> {omitted} lower-relevance node(s) were summarized without their full body to stay within the context budget (~{budget / 4} tokens of code).");
        }
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

/// <summary>
/// Controls capsule synthesis. <see cref="Unlimited"/> reproduces the legacy full-content, file-grouped
/// rendering. Setting <see cref="MaxContentChars"/> enables the budget-aware, seed-first renderer.
/// </summary>
public sealed record CapsuleOptions
{
    /// <summary>The retrieval seeds — rendered first and always in full.</summary>
    public IReadOnlyCollection<string>? SeedIds { get; init; }

    /// <summary>Approximate budget for code bodies in characters (~4 chars/token). Null = unlimited.</summary>
    public int? MaxContentChars { get; init; }

    /// <summary>Max number of nodes to render at all (diagram + content). Bounds 2-hop hub explosions.
    /// Null = no cap.</summary>
    public int? MaxNodes { get; init; }

    public static readonly CapsuleOptions Unlimited = new();
}
