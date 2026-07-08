// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Interfaces;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// Regression tests for BUG-007 (stale-file cleanup must not treat sibling directories with a shared
/// name prefix as "under this directory", and must still work for non-canonical directory inputs) and
/// BUG-015 (drift reconcile must converge: excluded-but-present files are removed, not re-indexed;
/// never-indexable files are not reported as drift forever).
/// </summary>
public class GraphIndexScannerDriftTests
{
    private static (SqliteGraphStorageProvider Storage, GraphIndexScanner Scanner) CreateScanner()
    {
        var storage = new SqliteGraphStorageProvider(":memory:");
        var scanner = new GraphIndexScanner(storage, new IFileParser[] { new RoslynAstParser() });
        return (storage, scanner);
    }

    [Fact]
    public async Task ScanDirectory_DoesNotDeleteSiblingDirectoryData_WithSharedNamePrefix()
    {
        var root = Path.Combine(Path.GetTempPath(), $"shonkor_prefix_{Guid.NewGuid():N}");
        var repo = Path.Combine(root, "Repo");
        var sibling = Path.Combine(root, "Repo2"); // shares the "Repo" name prefix
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(sibling);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(repo, "A.cs"), "namespace N { public class A { } }");
            var siblingFile = Path.Combine(sibling, "S.cs");
            await File.WriteAllTextAsync(siblingFile, "namespace N { public class S { } }");

            var (storage, scanner) = CreateScanner();
            using (storage)
            {
                await storage.InitializeAsync();
                // Index the sibling's file into the SAME graph (multi-root store), then scan only Repo.
                await scanner.ScanFileAsync(siblingFile);
                await scanner.ScanDirectoryAsync(repo, Array.Empty<string>());

                // Pre-fix, the raw StartsWith prefix check classified Repo2's file as "under Repo"
                // and deleted it. It must survive a scan of the sibling directory.
                Assert.NotNull(await storage.GetNodeByIdAsync(Path.GetFullPath(siblingFile)));
                Assert.NotNull(await storage.GetNodeByIdAsync(Path.GetFullPath(Path.Combine(repo, "A.cs"))));
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScanDirectory_NonCanonicalDirectoryInput_StillCleansUpDeletedFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"shonkor_noncanon_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "A.cs");
            await File.WriteAllTextAsync(file, "namespace N { public class A { } }");

            var (storage, scanner) = CreateScanner();
            using (storage)
            {
                await storage.InitializeAsync();
                await scanner.ScanDirectoryAsync(dir, Array.Empty<string>());
                Assert.NotNull(await storage.GetNodeByIdAsync(Path.GetFullPath(file)));

                // Delete on disk, then rescan with a NON-canonical path ("<dir>\."). Pre-fix the raw
                // StartsWith never matched a non-normalized input, silently disabling the cleanup.
                File.Delete(file);
                await scanner.ScanDirectoryAsync(Path.Combine(dir, "."), Array.Empty<string>());

                Assert.Null(await storage.GetNodeByIdAsync(Path.GetFullPath(file)));
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ReconcileDrift_RemovesExcludedButPresentFiles_AndConverges()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"shonkor_excl_{Guid.NewGuid():N}");
        var gen = Path.Combine(dir, "gen");
        Directory.CreateDirectory(gen);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "A.cs"), "namespace N { public class A { } }");
            var genFile = Path.Combine(gen, "G.cs");
            await File.WriteAllTextAsync(genFile, "namespace N { public class G { } }");

            var (storage, scanner) = CreateScanner();
            using (storage)
            {
                await storage.InitializeAsync();
                // Index WITHOUT excludes, then exclude gen/ and reconcile drift.
                await scanner.ScanDirectoryAsync(dir, Array.Empty<string>());
                Assert.NotNull(await storage.GetNodeByIdAsync(Path.GetFullPath(genFile)));

                var excludes = new[] { "gen/**" };
                var result = await scanner.ReconcileDriftAsync(dir, excludes);
                Assert.True(result.FilesScanned > 0);

                // Pre-fix, ScanFileAsync re-indexed the excluded-but-present file (it knows no exclude
                // patterns) and every subsequent drift pass reported it Deleted again — never converging.
                Assert.Null(await storage.GetNodeByIdAsync(Path.GetFullPath(genFile)));
                Assert.True((await scanner.DetectDriftAsync(dir, excludes)).IsClean);
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DetectDrift_DoesNotReportNeverIndexableNewFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"shonkor_bin_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "A.cs"), "namespace N { public class A { } }");

            var (storage, scanner) = CreateScanner();
            using (storage)
            {
                await storage.InitializeAsync();
                await scanner.ScanDirectoryAsync(dir, Array.Empty<string>());

                // A NEW binary file with a parseable extension: the scanner refuses it, so drift must
                // not report it either — otherwise reconcile chews on it every cycle, never clean.
                await File.WriteAllBytesAsync(Path.Combine(dir, "B.cs"), new byte[] { 0x4D, 0x5A, 0x00, 0x01, 0x00 });

                var drift = await scanner.DetectDriftAsync(dir, Array.Empty<string>());
                Assert.True(drift.IsClean);
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
