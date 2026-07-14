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
    /// Matches a single Markdown header LINE (e.g., <c># Title</c>, <c>## Section</c>). Captures the header
    /// level (number of <c>#</c>) and the text. Applied per line, not across the document, so that a
    /// <c>#</c> line inside a fenced code block can be skipped (a shell comment is not a section).
    /// </summary>
    [GeneratedRegex(@"^(#{1,6})\s+(.+)$")]
    private static partial Regex HeaderPattern();

    /// <summary>
    /// A fenced-code-block delimiter: at least three backticks or tildes at the start of a line, optionally
    /// followed by an info string. Captures the fence characters so a closing fence must match the opener.
    /// </summary>
    [GeneratedRegex(@"^\s{0,3}(`{3,}|~{3,})\s*(\S*)")]
    private static partial Regex FencePattern();

    /// <summary>
    /// Above <see cref="TokenBudget.SectionBudgetTokens"/> a section is split at paragraph boundaries into
    /// numbered sub-nodes, so a single sprawling section can't dominate retrieval — and, more importantly,
    /// so it stays inside the embedding model's window.
    /// <para>
    /// This used to be a flat <c>4000 characters</c> (#111). That is a fine proxy for English prose and a bad
    /// one for CJK (≈ 1 token per character, so 4000 chars is ~4000 tokens — silently truncated by the
    /// backend, with the tail of the section embedded into nothing) and for fenced code and tables, which
    /// tokenize far denser than prose. The budget is now measured in tokens, and English prose lands where it
    /// always did.
    /// </para>
    /// </summary>
    internal static int SectionBudgetTokens => TokenBudget.SectionBudgetTokens;

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

    /// <summary>A header found outside any fenced code block: its line index (0-based), level and title.</summary>
    private readonly record struct Header(int LineIndex, int Level, string Title);

    /// <summary>
    /// Finds the header lines that actually start a section: a <c>#</c> line inside a fenced code block
    /// (```/~~~) is a comment, not a boundary, so fences are tracked and their contents skipped. A closing
    /// fence must use the same character and be at least as long as the opener, and carry no info string.
    /// </summary>
    private static List<Header> FindHeaders(string[] lines)
    {
        var headers = new List<Header>();
        var fenceChar = '\0';
        var fenceLength = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var fence = FencePattern().Match(line);
            if (fence.Success)
            {
                var delimiter = fence.Groups[1].Value;
                if (fenceChar == '\0')
                {
                    fenceChar = delimiter[0];
                    fenceLength = delimiter.Length;
                    continue;
                }
                // A closing fence: same char, at least as long, and nothing after it.
                if (delimiter[0] == fenceChar && delimiter.Length >= fenceLength && fence.Groups[2].Value.Length == 0)
                {
                    fenceChar = '\0';
                    fenceLength = 0;
                }
                continue;
            }
            if (fenceChar != '\0') continue; // inside a code block — '#' is a comment, not a header

            var header = HeaderPattern().Match(line);
            if (header.Success)
            {
                headers.Add(new Header(i, header.Groups[1].Value.Length, header.Groups[2].Value.Trim()));
            }
        }
        return headers;
    }

    /// <summary>
    /// Creates a <c>MarkdownSection</c> node per header, carrying the section's own body (the text from its
    /// header line up to the next header, code fences and tables intact) and its 1-based line range, so the
    /// section — not just the whole file — is retrievable and citable. A section beyond
    /// <see cref="SectionBudgetTokens"/> is split at paragraph boundaries into numbered <c>::part::N</c>
    /// sub-nodes.
    ///
    /// <para>
    /// <b>CONTAINS follows the heading hierarchy</b> (#112): a <c>###</c> section is a child of its enclosing
    /// <c>##</c>, not a sibling of it. Only top-level headings hang directly off the file. So
    /// <c>get_subgraph</c> on a chapter reaches its subsections in one hop, and <c>outline</c> renders the
    /// real tree — where before, a query matching a child's detail could not reach the parent's framing, and
    /// vice versa, because nothing linked them.
    /// </para>
    ///
    /// <para>
    /// <b>The tension #112 posed, and how it is resolved:</b> nesting the <i>content</i> would mean a parent
    /// storing its children's text — duplicating it in FTS (double-counted by BM25) — or letting the parent's
    /// line range stop matching its content, which breaks the exactness that makes citations trustworthy.
    /// Neither is acceptable, so <b>only the edges nest</b>. A section's <c>Content</c> and
    /// <c>StartLine</c>–<c>EndLine</c> are exactly what they were: its own body, up to the next header of any
    /// level. Structure moves; text does not.
    /// </para>
    /// </summary>
    private static void ExtractSections(
        string filePath,
        string content,
        List<GraphNode> nodes,
        List<GraphEdge> edges)
    {
        var lines = content.Split('\n');
        var headers = FindHeaders(lines);

        // The chain of enclosing sections, innermost last. A header of level L is contained by the nearest
        // preceding header of a level strictly less than L; if there is none, the file contains it directly.
        var ancestors = new List<(int Level, string Id)>();

        for (var i = 0; i < headers.Count; i++)
        {
            var header = headers[i];
            // The section runs to the line before the next header (at any level), or to end of file.
            var endLineIndex = (i + 1 < headers.Count ? headers[i + 1].LineIndex : lines.Length) - 1;
            // Exclude the blank lines that merely separate this section from the next header, so the
            // reported StartLine..EndLine covers exactly the stored Content (citations must be exact).
            while (endLineIndex > header.LineIndex && string.IsNullOrWhiteSpace(lines[endLineIndex]))
            {
                endLineIndex--;
            }
            var sectionId = $"{filePath}::section::{i}::{header.Title}";

            // Pop every ancestor at or below this header's level — they are siblings or shallower, so they
            // cannot enclose it. (A document that jumps ## → #### still nests correctly: the #### simply
            // attaches to the ## it is actually under, rather than to a level-3 heading that never existed.)
            while (ancestors.Count > 0 && ancestors[^1].Level >= header.Level)
            {
                ancestors.RemoveAt(ancestors.Count - 1);
            }

            edges.Add(new GraphEdge
            {
                SourceId = ancestors.Count > 0 ? ancestors[^1].Id : filePath,
                TargetId = sectionId,
                Relationship = "CONTAINS"
            });

            ancestors.Add((header.Level, sectionId));

            var chunks = SplitAtParagraphs(lines, header.LineIndex, endLineIndex);
            for (var part = 0; part < chunks.Count; part++)
            {
                var (startIndex, endIndex, text) = chunks[part];
                // Part 0 keeps the section's own id, so existing handles and edges stay valid.
                var nodeId = part == 0 ? sectionId : $"{sectionId}::part::{part}";
                var name = part == 0 || chunks.Count == 1
                    ? header.Title
                    : $"{header.Title} (part {part + 1}/{chunks.Count})";

                var properties = new Dictionary<string, string>
                {
                    ["level"] = header.Level.ToString(),
                    ["index"] = i.ToString()
                };
                if (chunks.Count > 1)
                {
                    properties["part"] = part.ToString();
                    properties["parts"] = chunks.Count.ToString();
                }

                nodes.Add(new GraphNode
                {
                    Id = nodeId,
                    Name = name,
                    Type = "MarkdownSection",
                    FilePath = filePath,
                    Content = text,
                    StartLine = startIndex + 1, // 1-based (scheme v4 convention)
                    EndLine = endIndex + 1,
                    Properties = properties
                });

                if (part > 0)
                {
                    edges.Add(new GraphEdge
                    {
                        SourceId = sectionId,
                        TargetId = nodeId,
                        Relationship = "CONTAINS"
                    });
                }
            }
        }
    }

    /// <summary>
    /// Returns the section's line span as one chunk when it fits in <see cref="SectionBudgetTokens"/>, otherwise
    /// splits it at BLANK lines (paragraph boundaries) into chunks that each stay within the budget. A single
    /// paragraph larger than the budget is never cut mid-paragraph — it is emitted whole, because splitting a
    /// code fence or table in half would produce two unusable fragments.
    /// </summary>
    private static List<(int Start, int End, string Text)> SplitAtParagraphs(string[] lines, int startIndex, int endIndex)
    {
        var whole = Join(lines, startIndex, endIndex);
        if (TokenBudget.Fits(whole, TokenBudget.SectionBudgetTokens))
        {
            return new List<(int, int, string)> { (startIndex, endIndex, whole) };
        }

        // Paragraph starts: the header line, plus every line following a blank line.
        var boundaries = new List<int> { startIndex };
        for (var i = startIndex + 1; i <= endIndex; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i - 1]) && !string.IsNullOrWhiteSpace(lines[i]))
            {
                boundaries.Add(i);
            }
        }

        var chunks = new List<(int, int, string)>();
        var chunkStart = startIndex;
        for (var b = 1; b <= boundaries.Count; b++)
        {
            var nextStart = b < boundaries.Count ? boundaries[b] : endIndex + 1;
            var candidateEnd = nextStart - 1;

            var candidate = Join(lines, chunkStart, candidateEnd);
            var isLast = b == boundaries.Count;
            if (TokenBudget.Fits(candidate, TokenBudget.SectionBudgetTokens) && !isLast)
            {
                continue; // keep growing this chunk up to the budget
            }

            if (TokenBudget.Fits(candidate, TokenBudget.SectionBudgetTokens))
            {
                chunks.Add((chunkStart, candidateEnd, candidate));
                chunkStart = nextStart;
                continue;
            }

            // Over budget: close the chunk at the PREVIOUS paragraph boundary when there is one, so the
            // paragraph that pushed it over starts the next chunk instead of being torn apart.
            var priorEnd = boundaries[b - 1] - 1;
            if (priorEnd >= chunkStart)
            {
                chunks.Add((chunkStart, priorEnd, Join(lines, chunkStart, priorEnd)));
                chunkStart = boundaries[b - 1];
                b--; // re-evaluate this boundary against the new chunk start
                continue;
            }

            // A single paragraph exceeds the budget — emit it whole rather than cutting a fence/table.
            chunks.Add((chunkStart, candidateEnd, candidate));
            chunkStart = nextStart;
        }

        if (chunkStart <= endIndex)
        {
            chunks.Add((chunkStart, endIndex, Join(lines, chunkStart, endIndex)));
        }
        return chunks.Count > 0 ? chunks : new List<(int, int, string)> { (startIndex, endIndex, whole) };
    }

    private static string Join(string[] lines, int start, int end) =>
        end < start ? string.Empty : string.Join("\n", lines[start..(end + 1)]).TrimEnd();

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
