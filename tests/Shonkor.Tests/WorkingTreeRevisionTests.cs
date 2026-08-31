// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// AP8 stage 1 (#449): the graph records which revision it was built from, so an answer can say whether
/// it still matches the working tree. Until now the only staleness question the index could answer was
/// "did this one file change" — and it answered that with silence when the answer was no.
/// </summary>
public sealed class WorkingTreeRevisionTests : IDisposable
{
    private readonly List<string> _dirs = new();

    private string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "shonkor-rev-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        _dirs.Add(d);
        return d;
    }

    public void Dispose()
    {
        foreach (var d in _dirs)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { /* best effort */ }
        }
    }

    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    /// <summary>Read from files, never from a `git` process — the runtime image ships no git binary.</summary>
    [Fact]
    public void ReadsALooseRefThroughHead()
    {
        var dir = NewDir();
        Directory.CreateDirectory(Path.Combine(dir, ".git", "refs", "heads"));
        File.WriteAllText(Path.Combine(dir, ".git", "HEAD"), "ref: refs/heads/develop\n");
        File.WriteAllText(Path.Combine(dir, ".git", "refs", "heads", "develop"), Sha + "\n");

        Assert.Equal(Sha, WorkingTreeRevision.TryRead(dir));
    }

    [Fact]
    public void ReadsADetachedHead()
    {
        var dir = NewDir();
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        File.WriteAllText(Path.Combine(dir, ".git", "HEAD"), Sha + "\n");

        Assert.Equal(Sha, WorkingTreeRevision.TryRead(dir));
    }

    /// <summary>A ref that only exists packed still resolves; a fresh clone has no loose refs at all.</summary>
    [Fact]
    public void ReadsAPackedRef()
    {
        var dir = NewDir();
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        File.WriteAllText(Path.Combine(dir, ".git", "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(dir, ".git", "packed-refs"),
            "# pack-refs with: peeled fully-peeled sorted \n"
            + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa refs/heads/other\n"
            + Sha + " refs/heads/main\n"
            + "^bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\n");

        Assert.Equal(Sha, WorkingTreeRevision.TryRead(dir));
    }

    /// <summary>Scanning a subfolder still finds the repository it belongs to.</summary>
    [Fact]
    public void WalksUpToTheRepositoryRoot()
    {
        var dir = NewDir();
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        File.WriteAllText(Path.Combine(dir, ".git", "HEAD"), Sha + "\n");
        var sub = Path.Combine(dir, "src", "deep");
        Directory.CreateDirectory(sub);

        Assert.Equal(Sha, WorkingTreeRevision.TryRead(sub));
    }

    /// <summary>
    /// Not a repository is a third state, not a failure — and it must stay distinguishable from a match.
    /// Reporting "unknown" as "matches" would make the disclosure worse than not having one.
    /// </summary>
    [Fact]
    public void ReturnsNullWhenThereIsNoRepository()
    {
        Assert.Null(WorkingTreeRevision.TryRead(NewDir()));
        Assert.Null(WorkingTreeRevision.TryRead(null));
        Assert.Null(WorkingTreeRevision.TryRead(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid())));
    }

    /// <summary>Garbage in HEAD is not a revision. Guessing one would be worse than admitting ignorance.</summary>
    [Fact]
    public void ReturnsNullForAMalformedHead()
    {
        var dir = NewDir();
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        File.WriteAllText(Path.Combine(dir, ".git", "HEAD"), "not-a-sha\n");

        Assert.Null(WorkingTreeRevision.TryRead(dir));
    }

    /// <summary>The scan stamps what it read at the START, and a non-repository leaves the marker absent.</summary>
    [Fact]
    public async Task ScanStampsTheRevision_AndLeavesItAbsentOutsideARepository()
    {
        var dir = NewDir();
        await File.WriteAllTextAsync(Path.Combine(dir, "A.cs"), "public class A { }");

        using var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();
        var scanner = new GraphIndexScanner(storage, new[] { new Shonkor.Core.Services.RoslynAstParser() });

        await scanner.ScanDirectoryAsync(dir, Array.Empty<string>());
        Assert.Null(await storage.GetIndexedRevisionAsync());   // no .git → nothing claimed

        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        await File.WriteAllTextAsync(Path.Combine(dir, ".git", "HEAD"), Sha + "\n");
        await scanner.ScanDirectoryAsync(dir, Array.Empty<string>(), forceReparse: true);

        Assert.Equal(Sha, await storage.GetIndexedRevisionAsync());
    }
}
