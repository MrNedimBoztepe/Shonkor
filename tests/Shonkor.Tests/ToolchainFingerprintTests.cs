// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Infrastructure.Services;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// #408: the incremental scan's key covered only file CONTENT, so a corrected parser or a rebuilt plugin
/// left an existing graph untouched — every file looked unchanged. Measured on a real Sitecore solution: a
/// full rescan with the #402-corrected parser moved <b>0 of 1 679</b> wrongly-tiered edges.
///
/// <para>
/// The key now also carries a fingerprint of the toolchain that will interpret the files. These tests pin
/// the three properties that matter: it triggers on a changed parser set, it does NOT trigger otherwise, and
/// its result matches <c>--force</c> exactly — the regression pair that keeps #430 as a permanent oracle for
/// this mechanism rather than a one-off check.
/// </para>
/// </summary>
public sealed class ToolchainFingerprintTests : IDisposable
{
    private readonly List<string> _dirs = new();

    private const string Rel = "STUB_REL";

    /// <summary>Two distinct parser TYPES, standing in for "the parser assembly changed".</summary>
    private abstract class StubParserBase(Provenance tier) : IFileParser
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
                new GraphEdge[] { new() { SourceId = src, TargetId = tgt, Relationship = Rel, Provenance = tier } }));
        }
    }

    private sealed class StubParserV1() : StubParserBase(Provenance.Extracted);
    private sealed class StubParserV2() : StubParserBase(Provenance.Inferred);

    private string NewDirWithOneFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "shonkor-fingerprint-" + Guid.NewGuid().ToString("N"));
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

    private static Task<GraphIndexScanner.IndexResult> ScanAsync(
        SqliteGraphStorageProvider storage, string dir, IFileParser parser, bool force = false)
        => new GraphIndexScanner(storage, new[] { parser }).ScanDirectoryAsync(dir, Array.Empty<string>(), forceReparse: force);

    private static async Task<Provenance> TierAsync(SqliteGraphStorageProvider storage)
        => Assert.Single(await storage.GetAllEdgesAsync(), e => e.Relationship == Rel).Provenance;

    /// <summary>
    /// The acceptance criterion: a changed parser reaches an existing graph without any file changing and
    /// without <c>--force</c>. This is the assertion that was false before #408 — and false on a real
    /// solution, not only in a fixture.
    /// </summary>
    [Fact]
    public async Task ChangedParserSet_ReparsesAnUntouchedTree()
    {
        var dir = NewDirWithOneFile();
        using var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();

        await ScanAsync(storage, dir, new StubParserV1());
        Assert.Equal(Provenance.Extracted, await TierAsync(storage));

        // The parser is replaced. No file is touched. No --force.
        await ScanAsync(storage, dir, new StubParserV2());

        Assert.Equal(Provenance.Inferred, await TierAsync(storage));
    }

    /// <summary>
    /// The other side of the trade: an unchanged toolchain over an unchanged tree must still skip everything,
    /// or the incremental win has quietly been spent.
    /// </summary>
    [Fact]
    public async Task UnchangedToolchain_StillSkipsAnUnchangedTree()
    {
        var dir = NewDirWithOneFile();
        using var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();

        await ScanAsync(storage, dir, new StubParserV1());
        var second = await ScanAsync(storage, dir, new StubParserV1());

        Assert.Equal(1, second.FilesScanned);
        Assert.Equal(0, second.NodesCreated);
        Assert.Equal(0, second.EdgesCreated);
    }

    /// <summary>
    /// The regression pair, and the reason #430 stays after this lands: <c>--force</c> is the reference
    /// implementation of "reparse everything". Whatever the fingerprint path produces must equal it — any
    /// divergence is a dimension the key does not cover, and this is where it shows up rather than in a
    /// graph nobody re-measures.
    /// </summary>
    [Fact]
    public async Task FingerprintPath_AgreesWithForce_OnTheSameChange()
    {
        var viaFingerprint = NewDirWithOneFile();
        var viaForce = NewDirWithOneFile();

        using var storageA = new SqliteGraphStorageProvider(":memory:");
        using var storageB = new SqliteGraphStorageProvider(":memory:");
        await storageA.InitializeAsync();
        await storageB.InitializeAsync();

        await ScanAsync(storageA, viaFingerprint, new StubParserV1());
        await ScanAsync(storageB, viaForce, new StubParserV1());

        // A relies on the toolchain fingerprint; B is told to reparse regardless.
        await ScanAsync(storageA, viaFingerprint, new StubParserV2());
        await ScanAsync(storageB, viaForce, new StubParserV2(), force: true);

        Assert.Equal(await TierAsync(storageB), await TierAsync(storageA));

        static string Shape(IEnumerable<GraphEdge> edges) => string.Join('\n', edges
            .Select(e => $"{Path.GetFileName(e.SourceId)}|{Path.GetFileName(e.TargetId)}|{e.Relationship}|{e.Provenance}")
            .OrderBy(s => s, StringComparer.Ordinal));

        Assert.Equal(Shape(await storageB.GetAllEdgesAsync()), Shape(await storageA.GetAllEdgesAsync()));
    }

    /// <summary>
    /// A graph stamped before this existed reads back <c>null</c> — an unknown toolchain, which is treated as
    /// a changed one. That costs a single forced scan per legacy graph and is a no-op on an empty one, which
    /// is the right trade: the alternative is trusting a toolchain nobody recorded.
    /// </summary>
    [Fact]
    public async Task LegacyGraphWithNoFingerprint_IsTreatedAsChanged_AndThenStamped()
    {
        var dir = NewDirWithOneFile();
        using var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();

        Assert.Null(await storage.GetToolchainFingerprintAsync());

        await ScanAsync(storage, dir, new StubParserV1());

        var stamped = await storage.GetToolchainFingerprintAsync();
        Assert.False(string.IsNullOrWhiteSpace(stamped));

        // Stable across a repeat with the same toolchain — otherwise every scan would force the next one.
        await ScanAsync(storage, dir, new StubParserV1());
        Assert.Equal(stamped, await storage.GetToolchainFingerprintAsync());
    }
}
