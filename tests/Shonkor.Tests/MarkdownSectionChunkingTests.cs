// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Models;
using Shonkor.Core.Services;

namespace Shonkor.Tests;

/// <summary>
/// TICKET-211: MarkdownSection nodes carry their own body and 1-based line range, headers inside fenced
/// code blocks are not section boundaries, fences/tables survive chunking, and oversized sections split at
/// paragraph boundaries into numbered sub-nodes.
/// </summary>
public class MarkdownSectionChunkingTests
{
    private static string DocPath => OperatingSystem.IsWindows() ? @"C:\repo\doc.md" : "/repo/doc.md";

    private static async Task<(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)> ParseAsync(string content) =>
        await new MarkdownHierarchyParser().ParseAsync(DocPath, content);

    private static IReadOnlyList<GraphNode> Sections(IReadOnlyList<GraphNode> nodes) =>
        nodes.Where(n => n.Type == "MarkdownSection").ToList();

    [Fact]
    public async Task Section_CarriesItsOwnBody_NotJustTheTitle()
    {
        var (nodes, _) = await ParseAsync("# Intro\n\nHello world.\n\n## Setup\n\nRun the installer.\n");
        var sections = Sections(nodes);

        Assert.Equal(2, sections.Count);
        Assert.Contains("Hello world.", sections[0].Content);
        Assert.DoesNotContain("Run the installer.", sections[0].Content); // body stops at the next header
        Assert.Contains("Run the installer.", sections[1].Content);
    }

    [Fact]
    public async Task Section_LineRange_IsOneBased_AndCoversExactlyItsContent()
    {
        //          1         2  3             4  5              6  7
        var md = "# Intro\n\nHello.\n\n## Setup\n\nRun it.\n";
        var (nodes, _) = await ParseAsync(md);
        var sections = Sections(nodes);

        Assert.Equal(1, sections[0].StartLine);   // the header line itself
        Assert.Equal(3, sections[0].EndLine);     // trailing blank line before "## Setup" excluded
        Assert.Equal(5, sections[1].StartLine);

        // The stored range must reproduce the stored content from the file's lines.
        var lines = md.Split('\n');
        foreach (var s in sections)
        {
            var slice = string.Join("\n", lines[(s.StartLine!.Value - 1)..s.EndLine!.Value]).TrimEnd();
            Assert.Equal(slice, s.Content);
        }
    }

    [Fact]
    public async Task HeaderInsideFencedCodeBlock_IsNotASectionBoundary()
    {
        var md = """
                 # Real

                 ```bash
                 # this is a shell comment, not a header
                 echo hi
                 ```

                 Done.
                 """;
        var sections = Sections((await ParseAsync(md)).Nodes);

        Assert.Single(sections);
        Assert.Equal("Real", sections[0].Name);
        // The fence stays intact inside the section body.
        Assert.Contains("```bash", sections[0].Content);
        Assert.Contains("# this is a shell comment", sections[0].Content);
        Assert.Contains("Done.", sections[0].Content);
    }

    [Fact]
    public async Task TildeFence_AndLongerClosingFence_AreHandled()
    {
        var md = "# Top\n\n~~~\n# not a header\n~~~\n\n## Second\n\nbody\n";
        var sections = Sections((await ParseAsync(md)).Nodes);

        Assert.Equal(2, sections.Count);
        Assert.Equal("Top", sections[0].Name);
        Assert.Equal("Second", sections[1].Name);
    }

    [Fact]
    public async Task TableStaysIntact_WithinASection()
    {
        var md = "# Data\n\n| a | b |\n|---|---|\n| 1 | 2 |\n";
        var section = Sections((await ParseAsync(md)).Nodes).Single();

        Assert.Contains("| a | b |", section.Content);
        Assert.Contains("| 1 | 2 |", section.Content);
    }

    [Fact]
    public async Task OversizedSection_SplitsAtParagraphBoundaries_IntoNumberedParts()
    {
        // Many distinct paragraphs, together well over the 4k budget.
        var paragraph = string.Join(" ", Enumerable.Repeat("lorem ipsum dolor sit amet", 20));
        var body = string.Join("\n\n", Enumerable.Repeat(paragraph, 12));
        var md = $"# Big\n\n{body}\n";

        var (nodes, edges) = await ParseAsync(md);
        var sections = Sections(nodes);

        Assert.True(sections.Count > 1, "an oversized section must be split");
        // Part 0 keeps the section's own id, so existing handles stay valid.
        var sectionId = $"{DocPath}::section::0::Big";
        Assert.Equal(sectionId, sections[0].Id);
        Assert.All(sections.Skip(1), s => Assert.StartsWith($"{sectionId}::part::", s.Id));

        // Every chunk stays within the budget (no paragraph here exceeds it on its own).
        Assert.All(sections, s => Assert.True(s.Content.Length <= MarkdownSectionBudget, $"chunk of {s.Content.Length} chars"));

        // Parts are numbered and linked from the section.
        Assert.Equal(sections.Count.ToString(), sections[0].Properties["parts"]);
        Assert.Contains($"part 2/{sections.Count}", sections[1].Name);
        Assert.Equal("1", sections[1].Properties["part"]);
        Assert.Contains(edges, e => e.SourceId == sectionId && e.TargetId == sections[1].Id && e.Relationship == "CONTAINS");

        // Line ranges are contiguous and ascending across the parts.
        for (var i = 1; i < sections.Count; i++)
        {
            Assert.True(sections[i].StartLine > sections[i - 1].EndLine,
                $"part {i} must start after part {i - 1} ends");
        }
    }

    /// <summary>Mirrors MarkdownHierarchyParser.MaxSectionChars (internal const).</summary>
    private const int MarkdownSectionBudget = 4000;

    [Fact]
    public async Task SingleHugeParagraph_IsNotCutMidParagraph()
    {
        // One paragraph (a fenced block) larger than the budget: emitted whole rather than torn in half.
        var huge = "```\n" + string.Join("\n", Enumerable.Repeat("x = 1", 1200)) + "\n```";
        var md = $"# Huge\n\n{huge}\n";

        var sections = Sections((await ParseAsync(md)).Nodes);

        var withFence = sections.Single(s => s.Content.Contains("```", StringComparison.Ordinal));
        // Both the opening and the closing fence live in the same node — the block was not split.
        var fenceCount = withFence.Content.Split("```").Length - 1;
        Assert.Equal(2, fenceCount);
    }

    [Fact]
    public async Task SmallSection_ProducesNoPartProperties()
    {
        var section = Sections((await ParseAsync("# Small\n\nshort body\n")).Nodes).Single();

        Assert.False(section.Properties.ContainsKey("part"));
        Assert.False(section.Properties.ContainsKey("parts"));
    }

    [Fact]
    public async Task FileStillContainsEverySection()
    {
        var (nodes, edges) = await ParseAsync("# A\n\nx\n\n## B\n\ny\n");
        foreach (var s in Sections(nodes).Where(n => !n.Id.Contains("::part::", StringComparison.Ordinal)))
        {
            Assert.Contains(edges, e => e.SourceId == DocPath && e.TargetId == s.Id && e.Relationship == "CONTAINS");
        }
    }
}
