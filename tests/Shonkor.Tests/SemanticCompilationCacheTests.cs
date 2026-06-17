// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Interfaces;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// Tests the incremental compilation cache (drift P3 perf): it builds a per-directory compilation once and
/// swaps only the edited tree on later edits, and a cache-backed semantic reconcile refreshes CALLS.
/// </summary>
public class SemanticCompilationCacheTests
{
    private static string TreeText(Microsoft.CodeAnalysis.CSharp.CSharpCompilation? comp, string file) =>
        comp!.SyntaxTrees.First(t => t.FilePath.EndsWith(file, StringComparison.OrdinalIgnoreCase)).ToString();

    [Fact]
    public async Task ApplyEdits_SwapsTheEditedTree_ReflectingNewContent()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"shonkor_compcache_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var aFile = Path.Combine(dir, "S.cs");
        try
        {
            await File.WriteAllTextAsync(aFile, "namespace N { public class S { public void Run() { } } }");

            var cache = new SemanticCompilationCache();
            var built = await cache.GetOrBuildAsync(dir);
            Assert.NotNull(built);
            Assert.DoesNotContain("Helper", TreeText(built, "S.cs"));

            // Edit on disk, then apply only that file: the cached compilation's tree must reflect it.
            await File.WriteAllTextAsync(aFile, "namespace N { public class S { public void Run() { Helper(); } public void Helper() { } } }");
            var updated = await cache.ApplyEditsAsync(dir, new[] { aFile });
            Assert.Contains("Helper", TreeText(updated, "S.cs"));

            // After invalidation, a rebuild reflects current disk too.
            cache.Invalidate(dir);
            var rebuilt = await cache.GetOrBuildAsync(dir);
            Assert.Contains("Helper", TreeText(rebuilt, "S.cs"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CacheBackedReconcile_RefreshesCallsEdge()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"shonkor_compcache_reconcile_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var aFile = Path.Combine(dir, "Svc.cs");
        try
        {
            await File.WriteAllTextAsync(aFile, "namespace N { public class Svc { public void Run() { } public void Helper() { } } }");

            using var storage = new SqliteGraphStorageProvider(":memory:");
            await storage.InitializeAsync();
            var cache = new SemanticCompilationCache();
            var scanner = new GraphIndexScanner(storage, new IFileParser[] { new RoslynAstParser() }, logger: null, semanticCsharp: true, compilationCache: cache);
            await scanner.ScanDirectoryAsync(dir, Array.Empty<string>());

            var helperId = Path.GetFullPath(aFile) + "::Svc::Helper#0";
            var runId = Path.GetFullPath(aFile) + "::Svc::Run#0";

            await File.WriteAllTextAsync(aFile, "namespace N { public class Svc { public void Run() { Helper(); } public void Helper() { } } }");
            await scanner.ReconcilePathsAsync(dir, new[] { "Svc.cs" });

            var (edges, _) = await storage.GetIncidentEdgesAsync(helperId);
            Assert.Contains(edges, e => e.SourceId == runId && e.Relationship == "CALLS");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
