// Licensed to Shonkor under the MIT License.

using System.Text.Json;

using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;
using Shonkor.Infrastructure.Storage;
using Shonkor.Web;

namespace Shonkor.Tests;

/// <summary>
/// Regression cover for the #292 web-MCP-relay data-loss fix. Since the JS/TS parser moved out of the host
/// DI list into the <c>shonkor-typescript</c> plugin, the relay MUST merge the active plugin parsers into
/// the list it hands <c>reindex_file</c>; otherwise a <c>reindex_file</c> of a <c>.ts/.tsx/.js/.jsx</c> file
/// finds no parser, produces zero nodes, and <c>ScanFileAsync</c> then CLEARS the file's existing graph
/// nodes — silent data loss.
/// </summary>
public class McpRelayPluginParserTests
{
    // ---- The endpoint's parser-list construction (the single place the merge lives) ----

    [Fact]
    public void BuildRelayFileParsers_NotTenantLocked_IncludesActivePluginParsers()
    {
        var baseParsers = new List<IFileParser> { new RoslynAstParser() };
        var pluginParsers = new List<IFileParser> { new FakeTsParser() };

        var result = EndpointHelpers.BuildRelayFileParsers(
            isTenantLocked: false, baseParsers, pluginParsers);

        Assert.NotNull(result);
        var list = result!.ToList();
        // The base parser AND the active plugin parser are both present — the plugin (which supplies the
        // JS/TS parser) is not dropped.
        Assert.Contains(list, p => p is RoslynAstParser);
        Assert.Contains(list, p => p is FakeTsParser);
    }

    [Fact]
    public void BuildRelayFileParsers_TenantLocked_ReturnsNull_DisablingReindex()
    {
        var result = EndpointHelpers.BuildRelayFileParsers(
            isTenantLocked: true,
            new List<IFileParser> { new RoslynAstParser() },
            new List<IFileParser> { new FakeTsParser() });

        // Tenant-locked (SaaS): no parsers -> reindex_file stays disabled, never touching a tenant's graph.
        Assert.Null(result);
    }

    // ---- The consequence the fix prevents: reindex of a .ts file must not delete its nodes ----

    [Fact]
    public async Task ReindexFile_WithoutJsTsParser_ClearsTheFilesNodes()
    {
        var (pm, synth, projectDir, db, tsPath) = await SetupProjectWithSeededTsNodeAsync();

        // Parser list WITHOUT any .ts parser — the exact broken state the relay was in after JS/TS moved to
        // the plugin and the plugins were NOT merged.
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true,
            fileParsers: new List<IFileParser> { new RoslynAstParser() }, compilationCache: null);

        var response = await handler.ProcessJsonRpcMessageAsync(ReindexCall(Path.GetFileName(tsPath)));

        Assert.NotNull(response);
        Assert.Contains("Cleared", response!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await NodeCountForFileAsync(db, tsPath)); // the seeded JSComponent node is gone
    }

    [Fact]
    public async Task ReindexFile_WithJsTsParser_KeepsTheFilesNodes()
    {
        var (pm, synth, projectDir, db, tsPath) = await SetupProjectWithSeededTsNodeAsync();

        // Parser list WITH a .ts parser — what merging the active shonkor-typescript plugin provides.
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true,
            fileParsers: new List<IFileParser> { new RoslynAstParser(), new FakeTsParser() }, compilationCache: null);

        var response = await handler.ProcessJsonRpcMessageAsync(ReindexCall(Path.GetFileName(tsPath)));

        Assert.NotNull(response);
        Assert.Contains("Reindexed", response!, StringComparison.OrdinalIgnoreCase);
        Assert.True(await NodeCountForFileAsync(db, tsPath) > 0); // the file still has graph presence
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

    private static async Task<(ProjectManager Pm, ContextCapsuleSynthesizer Synth, string ProjectDir, string Db, string TsPath)>
        SetupProjectWithSeededTsNodeAsync()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_relay_{Guid.NewGuid():N}");
        var projectDir = Path.Combine(ws, "proj");
        Directory.CreateDirectory(projectDir);
        var db = Path.Combine(ws, "g.db");

        var tsPath = Path.Combine(projectDir, "App.ts");
        await File.WriteAllTextAsync(tsPath, "export const App = 1;\n");
        var fullPath = Path.GetFullPath(tsPath);

        // Simulate a prior index: the file already has a JSComponent node in the graph.
        using (var storage = new SqliteGraphStorageProvider(db))
        {
            await storage.InitializeAsync();
            await storage.UpsertNodesAsync(new[]
            {
                new GraphNode { Id = $"{fullPath}::App", Name = "App", Type = "JSComponent", FilePath = fullPath }
            });
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

        return (new ProjectManager(ws), new ContextCapsuleSynthesizer(), projectDir, db, tsPath);
    }

    private static async Task<int> NodeCountForFileAsync(string db, string filePath)
    {
        using var storage = new SqliteGraphStorageProvider(db);
        await storage.InitializeAsync();
        var nodes = await storage.GetNodesByFilePathAsync(Path.GetFullPath(filePath));
        return nodes.Count;
    }

    /// <summary>A stand-in for the plugin's JS/TS parser: it claims <c>.ts</c> so reindex has a parser.</summary>
    private sealed class FakeTsParser : IFileParser
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".ts", ".tsx" };
        public Provenance DefaultProvenance => Provenance.Inferred;
        public IReadOnlyList<NodeTypeDescriptor> NodeTypeDescriptors { get; } = new[] { new NodeTypeDescriptor("JSComponent", "Code", true) };

        public Task<(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)> ParseAsync(string filePath, string content)
        {
            var name = Path.GetFileNameWithoutExtension(filePath);
            var nodes = new List<GraphNode>
            {
                new GraphNode { Id = $"{filePath}::{name}", Name = name, Type = "JSComponent", FilePath = filePath }
            };
            return Task.FromResult<(IReadOnlyList<GraphNode>, IReadOnlyList<GraphEdge>)>((nodes, new List<GraphEdge>()));
        }
    }
}
