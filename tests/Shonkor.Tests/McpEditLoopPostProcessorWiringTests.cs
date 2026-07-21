// Licensed to Shonkor under the MIT License.

using System.Text.Json;

using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// Regression cover for #319: the MCP edit-loop scan path (<c>reindex_file</c>) constructs its
/// <see cref="GraphIndexScanner"/> with the active plugins' <see cref="IGraphPostProcessor"/>s, exactly like
/// the Web/CLI index — closing the wiring inconsistency.
///
/// <para>
/// The honest semantics this pins (AC#2): graph post-processors are a WHOLE-GRAPH phase. The scanner runs
/// them on a full <see cref="GraphIndexScanner.ScanDirectoryAsync"/> only, NEVER on a single-file reindex
/// (<see cref="GraphIndexScanner.ScanFileAsync"/> / <see cref="GraphIndexScanner.ReconcilePathsAsync"/>).
/// So over the MCP edit loop the post-processors are wired through the context and carried into the scanner,
/// but deliberately do not execute per edited file — they take effect on the next full scan. This mirrors
/// the drift reconcile path, which passes them the same way for construction consistency without running
/// them per file.
/// </para>
/// </summary>
public class McpEditLoopPostProcessorWiringTests
{
    // ---- Scanner-level: the whole-graph-only gating the reindex tool relies on ----

    [Fact]
    public async Task PostProcessor_RunsOnFullScan_ButNotOnSingleFileReindex()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"shonkor_pp_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "A.cs");
            await File.WriteAllTextAsync(file, "namespace N { public class A { } }");

            using var storage = new SqliteGraphStorageProvider(":memory:");
            await storage.InitializeAsync();

            var pp = new RecordingPostProcessor();
            var scanner = new GraphIndexScanner(storage, new IFileParser[] { new RoslynAstParser() },
                postProcessors: new IGraphPostProcessor[] { pp });

            // Single-file reindex (the MCP edit-loop path) must NOT run the whole-graph post-processor.
            await scanner.ScanFileAsync(file);
            Assert.Equal(0, pp.Invocations);
            Assert.Null(await storage.GetNodeByIdAsync(RecordingPostProcessor.EnrichmentNodeId));

            // A full scan DOES run it — the passed post-processors are live where the phase belongs
            // (i.e. the wiring is not dead code).
            await scanner.ScanDirectoryAsync(dir, Array.Empty<string>());
            Assert.Equal(1, pp.Invocations);
            Assert.NotNull(await storage.GetNodeByIdAsync(RecordingPostProcessor.EnrichmentNodeId));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // ---- MCP-level: reindex_file carries the post-processors but does not execute them per file ----

    [Fact]
    public async Task ReindexFile_CarriesPostProcessors_ButDoesNotRunThemOnSingleFile()
    {
        var (pm, synth, db, csPath) = await SetupProjectWithCsFileAsync();

        var pp = new RecordingPostProcessor();
        var handler = new McpRequestHandler(pm, synth, "P",
            fileParsers: new List<IFileParser> { new RoslynAstParser() }, compilationCache: null,
            postProcessors: new IGraphPostProcessor[] { pp });

        var response = await handler.ProcessJsonRpcMessageAsync(ReindexCall(Path.GetFileName(csPath)));

        Assert.NotNull(response);
        // The reindex succeeds (the .cs file parses into nodes) ...
        Assert.Contains("Reindexed", response!, StringComparison.OrdinalIgnoreCase);
        // ... yet the whole-graph post-processor, though wired through the context, does not execute on the
        // single-file reindex path — the consistent semantics #319 pins.
        Assert.Equal(0, pp.Invocations);

        using var check = new SqliteGraphStorageProvider(db);
        await check.InitializeAsync();
        Assert.Null(await check.GetNodeByIdAsync(RecordingPostProcessor.EnrichmentNodeId));
    }

    // ---- helpers ----

    private static string ReindexCall(string relativePath) =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = "reindex_file", arguments = new { path = relativePath, projectName = "P" } }
        });

    private static async Task<(ProjectManager Pm, ContextCapsuleSynthesizer Synth, string Db, string CsPath)>
        SetupProjectWithCsFileAsync()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_pp_mcp_{Guid.NewGuid():N}");
        var projectDir = Path.Combine(ws, "proj");
        Directory.CreateDirectory(projectDir);
        var db = Path.Combine(ws, "g.db");

        var csPath = Path.Combine(projectDir, "A.cs");
        await File.WriteAllTextAsync(csPath, "namespace N { public class A { } }");

        using (var storage = new SqliteGraphStorageProvider(db))
        {
            await storage.InitializeAsync();
        }

        var registry = new
        {
            Organizations = Array.Empty<object>(),
            Users = Array.Empty<object>(),
            Projects = new[]
            {
                new { Name = "P", Path = projectDir, DatabasePath = db, OrganizationId = "", RepositoryUrl = "", ApiKey = "" }
            },
            ActiveProjectName = "P"
        };
        await File.WriteAllTextAsync(Path.Combine(ws, "projects.json"), JsonSerializer.Serialize(registry));

        return (new ProjectManager(ws), new ContextCapsuleSynthesizer(), db, csPath);
    }

    /// <summary>
    /// A whole-graph post-processor that records how many times the host invoked it and emits one additive
    /// enrichment node — so a test can assert both non-execution (single-file) and execution (full scan).
    /// </summary>
    private sealed class RecordingPostProcessor : IGraphPostProcessor
    {
        public const string EnrichmentNodeId = "test::pp-enrichment";

        private int _invocations;
        public int Invocations => _invocations;

        public string Name => "test.recording";

        public Task<GraphEnrichment> ProcessAsync(IGraphView graph)
        {
            Interlocked.Increment(ref _invocations);
            var node = new GraphNode { Id = EnrichmentNodeId, Name = "Enrichment", Type = "TestEnrichment" };
            return Task.FromResult(new GraphEnrichment(
                new[] { node }, Array.Empty<GraphEdge>(), Array.Empty<GraphDiagnostic>()));
        }
    }
}
