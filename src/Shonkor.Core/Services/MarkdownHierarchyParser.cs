using System.Collections.Frozen;
using System.Text.RegularExpressions;

using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;

namespace Shonkor.Core.Services;

/// <summary>
/// Parses Markdown and plain-text files to extract a hierarchy of sections
/// and detect relative file references within link syntax.
/// </summary>
public sealed partial class MarkdownHierarchyParser : IFileParser
{
    /// <summary>
    /// Matches Markdown header lines (e.g., <c># Title</c>, <c>## Section</c>, <c>### Subsection</c>).
    /// Captures the header level (number of <c>#</c> characters) and the header text.
    /// </summary>
    [GeneratedRegex(@"^(#{1,6})\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex HeaderPattern();

    /// <summary>
    /// Matches Markdown inline links with relative paths (e.g., <c>[text](./path/to/file.md)</c>).
    /// Excludes links starting with <c>http</c>, <c>https</c>, or <c>#</c> (anchors).
    /// </summary>
    [GeneratedRegex(@"\[([^\]]+)\]\((?!https?://|#)([^)]+)\)", RegexOptions.Compiled)]
    private static partial Regex RelativeLinkPattern();

    /// <inheritdoc />
    /// <inheritdoc />
    /// <remarks>Regex-based Markdown extraction — heuristic (TICKET-207). Structural CONTAINS edges stay
    /// Extracted via the scanner's structural-edge exemption; link REFERENCES are Inferred.</remarks>
    public Provenance DefaultProvenance => Provenance.Inferred;

    public IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".md", ".mdx" }.ToFrozenSet();

    /// <inheritdoc />
    public IReadOnlyList<NodeTypeDescriptor> NodeTypeDescriptors { get; } = new[]
    {
        new NodeTypeDescriptor("MarkdownSection", "Documentation", true)
    };

    /// <inheritdoc />
    public Task<(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)> ParseAsync(
        string filePath,
        string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(content);

        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();

        ExtractSections(filePath, content, nodes, edges);
        ExtractRelativeLinks(filePath, content, edges);

        return Task.FromResult<(IReadOnlyList<GraphNode>, IReadOnlyList<GraphEdge>)>(
            (nodes.AsReadOnly(), edges.AsReadOnly()));
    }

    /// <summary>
    /// Splits the content by Markdown header lines and creates a <c>MarkdownSection</c> node
    /// for each section, along with a <c>CONTAINS</c> edge from the file node to each section.
    /// </summary>
    private static void ExtractSections(
        string filePath,
        string content,
        List<GraphNode> nodes,
        List<GraphEdge> edges)
    {
        var matches = HeaderPattern().Matches(content);

        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var level = match.Groups[1].Value.Length;
            var title = match.Groups[2].Value.Trim();
            var sectionId = $"{filePath}::section::{i}::{title}";

            nodes.Add(new GraphNode
            {
                Id = sectionId,
                Name = title,
                Type = "MarkdownSection",
                FilePath = filePath,
                Properties = new Dictionary<string, string>
                {
                    ["level"] = level.ToString(),
                    ["index"] = i.ToString()
                }
            });

            edges.Add(new GraphEdge
            {
                SourceId = filePath,
                TargetId = sectionId,
                Relationship = "CONTAINS"
            });
        }
    }

    /// <summary>
    /// Detects relative file links in the Markdown content and creates <c>REFERENCES</c> edges
    /// from the current file to each referenced path.
    /// </summary>
    private static void ExtractRelativeLinks(
        string filePath,
        string content,
        List<GraphEdge> edges)
    {
        var processedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in RelativeLinkPattern().Matches(content))
        {
            var targetPath = match.Groups[2].Value.Trim();

            // Skip fragment-only links and already-processed targets to avoid duplicates
            if (string.IsNullOrEmpty(targetPath) || !processedTargets.Add(targetPath))
            {
                continue;
            }

            // Resolve relative path against the directory of the current file
            var directory = Path.GetDirectoryName(filePath) ?? string.Empty;
            var resolvedPath = Path.GetFullPath(Path.Combine(directory, targetPath));

            edges.Add(new GraphEdge
            {
                SourceId = filePath,
                TargetId = resolvedPath,
                Relationship = "REFERENCES",
                Properties = new Dictionary<string, string>
                {
                    ["linkText"] = match.Groups[1].Value,
                    ["rawTarget"] = targetPath
                }
            });
        }
    }
}
