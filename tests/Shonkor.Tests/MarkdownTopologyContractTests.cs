// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Models;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// The topology contract for nested Markdown sections (#175).
/// <para>
/// #112 changed <c>CONTAINS</c> from a flat <c>File → section</c> fan-out to a nested
/// <c>File → h1 → h2 → h3</c> tree. <c>outline</c> silently broke on that change — it fetched a fixed 2-hop
/// subgraph and just stopped showing <c>###</c> sections, with <b>no failing test</b>. It was caught only by
/// eyeballing real output.
/// </para>
/// <para>
/// The lesson of #175 is not "nesting was wrong" — it is that a topology change had <b>no failing-test
/// surface</b>. These tests are that surface. They pin the two invariants an audit proved are load-bearing
/// (re-index/delete cleanup is FilePath-scoped, not CONTAINS-walk-scoped) and the one behaviour that is
/// depth-sensitive (a fixed-hop file seed under-reaches a deep section) — so the next change to either fails
/// loudly and deliberately instead of degrading in silence.
/// </para>
/// </summary>
public class MarkdownTopologyContractTests
{
    private static SqliteGraphStorageProvider NewStorage()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"shonkor_topo_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return new SqliteGraphStorageProvider(Path.Combine(dir, "g.db"));
    }

    /// <summary>Parses a markdown doc and upserts it — plus its File node — exactly as the scanner would.</summary>
    private static async Task IndexDocAsync(SqliteGraphStorageProvider storage, string filePath, string markdown)
    {
        var (nodes, edges) = await new MarkdownHierarchyParser().ParseAsync(filePath, markdown);
        await storage.UpsertNodesAsync(
            nodes.Append(new GraphNode { Id = filePath, Name = Path.GetFileName(filePath), Type = "File", FilePath = filePath, Content = "" }));
        await storage.UpsertEdgesAsync(edges);
    }

    private static async Task<int> SectionCountAsync(SqliteGraphStorageProvider storage) =>
        (await storage.GetStatisticsAsync()).NodesByType.GetValueOrDefault("MarkdownSection", 0);

    private const string DeepDoc =
        "# Top\nintro\n## Chapter A\nframing a\n### Detail A1\nx\n### Detail A2\ny\n## Chapter B\nframing b\n### Detail B1\nz\n";
    // Sections: Top, Chapter A, Detail A1, Detail A2, Chapter B, Detail B1 = 6.

    // ---- Invariant 1: re-index cleanup is FilePath-scoped, so nesting can't strand ghost sections --------

    [Fact]
    public async Task ReindexingAShrunkNestedDoc_LeavesNoOrphanSections()
    {
        using var storage = NewStorage();
        await storage.InitializeAsync();
        const string path = "/docs/doc.md";

        await IndexDocAsync(storage, path, DeepDoc);
        Assert.Equal(6, await SectionCountAsync(storage));

        // Shrink to Top, Chapter A, Detail A1, Chapter B = 4, dropping two deep sections.
        await storage.ClearFileForReindexAsync(path);
        await IndexDocAsync(storage, path, "# Top\nintro\n## Chapter A\nframing a\n### Detail A1\nx\n## Chapter B\nframing b\n");

        // The danger the audit chased: nested h2/h3 nodes whose PARENT is a section, not the file, surviving a
        // cleanup that only knew about direct file children. They don't — cleanup deletes by FilePath, which
        // every section carries at every level. If this ever regresses to a CONTAINS-walk cleanup, it fails here.
        Assert.Equal(4, await SectionCountAsync(storage));
    }

    [Fact]
    public async Task DeletingAFile_RemovesEverySectionAtEveryDepth()
    {
        using var storage = NewStorage();
        await storage.InitializeAsync();
        const string path = "/docs/doc.md";

        await IndexDocAsync(storage, path, DeepDoc);
        Assert.Equal(6, await SectionCountAsync(storage));

        await storage.DeleteByFilePathAsync(path);
        Assert.Equal(0, await SectionCountAsync(storage));
    }

    // ---- Invariant 2: the depth-sensitive behaviour, pinned so a future change is loud --------------------

    [Fact]
    public async Task NestedSection_IsReachableFromItsFile_ByAFullContainsWalk()
    {
        // The contract `outline` (and any "show me this document's structure" consumer) depends on: every
        // section, however deep, is connected to its file through a chain of CONTAINS edges. This is what a
        // breadth-first CONTAINS walk relies on; if nesting ever detaches a level, this fails.
        using var storage = NewStorage();
        await storage.InitializeAsync();
        const string path = "/docs/doc.md";
        await IndexDocAsync(storage, path, DeepDoc);

        var reachable = new HashSet<string>(StringComparer.Ordinal) { path };
        var frontier = new Queue<string>([path]);
        while (frontier.Count > 0)
        {
            var (incident, _) = await storage.GetIncidentEdgesAsync(frontier.Dequeue());
            foreach (var e in incident.Where(e => e.Relationship == "CONTAINS"))
                if (reachable.Add(e.TargetId)) frontier.Enqueue(e.TargetId);
        }

        // All six sections — including the three `###` — must be reachable.
        Assert.Equal(7, reachable.Count); // file + 6 sections
    }

    [Fact]
    public async Task FileSeededSubgraph_AtTwoHops_UnderReachesDeepSections_ByDesignForNow()
    {
        // THE #175 behaviour, pinned deliberately. A `###` is now 3 undirected hops from its file
        // (File → h1 → h2 → h3), so a fixed 2-hop file-seeded subgraph — the default for generate_capsule,
        // /api/capsule and /api/rag/query — reaches h1 and h2 but NOT h3.
        //
        // This is not asserting the behaviour is GOOD (it is a completeness regression for deeply nested
        // docs, tracked separately). It is asserting it is KNOWN: if anyone changes the default hop budget,
        // the traversal semantics, or the nesting depth, this test changes with them — visibly and on
        // purpose — instead of a capsule silently losing a document's detail as `outline` once did.
        using var storage = NewStorage();
        await storage.InitializeAsync();
        const string path = "/docs/doc.md";
        await IndexDocAsync(storage, path, DeepDoc);

        var (nodes, _) = await storage.GetSubgraphAsync(new[] { path }, 2);
        var names = nodes.Select(n => n.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("Top", names);        // h1, depth 1
        Assert.Contains("Chapter A", names);  // h2, depth 2
        Assert.DoesNotContain("Detail A1", names); // h3, depth 3 — outside the 2-hop window

        // Raising the budget to the depth reaches it — the caller's escape hatch, and proof the data is there.
        var (deep, _) = await storage.GetSubgraphAsync(new[] { path }, 3);
        Assert.Contains("Detail A1", deep.Select(n => n.Name));
    }
}
