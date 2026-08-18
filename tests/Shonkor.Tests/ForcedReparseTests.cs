// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Infrastructure.Services;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// #430: the content hash answers "did this FILE change" and nothing else. When the code that INTERPRETS a
/// file changes — a corrected parser, a rebuilt plugin, a fixed post-processor — every file looks unchanged
/// and the correction never reaches the graph.
///
/// <para>
/// That is not a hypothesis. A full rescan of a real Sitecore solution with the #402-corrected parser moved
/// <b>0 of 1 679</b> wrongly-tiered heritage edges, because not one source file had changed. These tests pin
/// both halves: that a normal scan cannot fix it, and that a forced one can.
/// </para>
/// </summary>
public sealed class ForcedReparseTests : IDisposable
{
    private readonly List<string> _dirs = new();

    /// <summary>A parser whose verdict about the same file can change between scans, like a corrected one.</summary>
    private sealed class VersionedStubParser(Provenance edgeTier) : IFileParser
    {
        public IReadOnlySet<string> SupportedExtensions { get; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".stub" };

        public IReadOnlyList<NodeTypeDescriptor> NodeTypeDescriptors { get; } =
            new[] { new NodeTypeDescriptor("StubType", "Code", true) };

        public Task<(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)> ParseAsync(string filePath, string content)
        {
            var src = filePath + "::Src";
            var tgt = filePath + "::Tgt";
            return Task.FromResult<(IReadOnlyList<GraphNode>, IReadOnlyList<GraphEdge>)>((
                new GraphNode[]
                {
                    new() { Id = src, Type = "StubType", Name = "Src", FilePath = filePath, Content = content },
                    new() { Id = tgt, Type = "StubType", Name = "Tgt", FilePath = filePath, Content = content },
                },
                new GraphEdge[]
                {
                    new() { SourceId = src, TargetId = tgt, Relationship = "STUB_REL", Provenance = edgeTier },
                }));
        }
    }

    private string NewDirWithOneFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "shonkor-force-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        File.WriteAllText(Path.Combine(dir, "a.stub"), "content that never changes");
        return dir;
    }

    public void Dispose()
    {
        foreach (var d in _dirs)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { /* best effort */ }
        }
    }

    private static async Task ScanAsync(SqliteGraphStorageProvider storage, string dir, Provenance tier, bool force)
        => await new GraphIndexScanner(storage, new IFileParser[] { new VersionedStubParser(tier) })
            .ScanDirectoryAsync(dir, Array.Empty<string>(), forceReparse: force);

    private static async Task<Provenance> TierAsync(SqliteGraphStorageProvider storage)
        => Assert.Single(await storage.GetAllEdgesAsync(), e => e.Relationship == "STUB_REL").Provenance;

    /// <summary>
    /// The gap, stated as a test rather than as prose: a corrected parser plus an untouched file equals no
    /// correction. This is the assertion that makes the flag necessary — if it ever starts failing, the flag
    /// has become redundant and should be reconsidered rather than kept out of habit.
    /// </summary>
    [Fact]
    public async Task NormalScan_CannotApplyACorrectedParser_WhenNoFileChanged()
    {
        var dir = NewDirWithOneFile();
        using var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();

        await ScanAsync(storage, dir, Provenance.Extracted, force: false);
        Assert.Equal(Provenance.Extracted, await TierAsync(storage));

        // The parser is corrected. The file is not touched.
        await ScanAsync(storage, dir, Provenance.Inferred, force: false);

        Assert.Equal(Provenance.Extracted, await TierAsync(storage));   // still wrong
    }

    /// <summary>The other half: the same corrected parser, forced, converges.</summary>
    [Fact]
    public async Task ForcedScan_AppliesACorrectedParser_WithoutAnyFileChanging()
    {
        var dir = NewDirWithOneFile();
        using var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();

        await ScanAsync(storage, dir, Provenance.Extracted, force: false);
        Assert.Equal(Provenance.Extracted, await TierAsync(storage));

        await ScanAsync(storage, dir, Provenance.Inferred, force: true);

        Assert.Equal(Provenance.Inferred, await TierAsync(storage));
    }

    /// <summary>
    /// The flag must be an escape hatch, not a mode: without it nothing changes, so the incremental win is
    /// not quietly traded away by adding it.
    /// </summary>
    [Fact]
    public async Task WithoutTheFlag_AnUnchangedTreeIsStillSkipped()
    {
        var dir = NewDirWithOneFile();
        using var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();

        await ScanAsync(storage, dir, Provenance.Extracted, force: false);

        var second = await new GraphIndexScanner(storage, new IFileParser[] { new VersionedStubParser(Provenance.Extracted) })
            .ScanDirectoryAsync(dir, Array.Empty<string>());

        Assert.Equal(1, second.FilesScanned);   // the file was looked at...
        Assert.Equal(0, second.NodesCreated);   // ...and not reparsed
        Assert.Equal(0, second.EdgesCreated);
    }

    /// <summary>
    /// A forced scan really does reparse rather than merely claim to: the same run reports work where the
    /// incremental one reported none. Pinned because "reported success" and "did something" are exactly the
    /// two things a scan currently cannot tell apart (#423).
    /// </summary>
    [Fact]
    public async Task ForcedScan_ReportsTheWorkAnIncrementalScanSkips()
    {
        var dir = NewDirWithOneFile();
        using var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();

        await ScanAsync(storage, dir, Provenance.Extracted, force: false);

        var forced = await new GraphIndexScanner(storage, new IFileParser[] { new VersionedStubParser(Provenance.Extracted) })
            .ScanDirectoryAsync(dir, Array.Empty<string>(), forceReparse: true);

        Assert.True(forced.NodesCreated > 0, "a forced scan must reparse, not report a no-op");
        Assert.True(forced.EdgesCreated > 0);
    }
}
