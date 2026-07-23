// Licensed to Shonkor under the MIT License.

using System.Runtime.Loader;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;
using Shonkor.Infrastructure.Storage;
using Shonkor.Web;
using Shonkor.Web.Endpoints;

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

    // ---- The endpoint WIRING itself: McpEndpoints.RelayAsync must merge the active plugin parsers ----
    // This drives the real relay handler (not the isolated helper) against a non-tenant-locked context — the
    // branch the HTTP pipeline can never reach, since ApiKeyMiddleware always tenant-locks /api/mcp. It proves
    // the endpoint actually merges the active plugin parser into the list it hands reindex_file: with the
    // plugin's .ts parser present the seeded node survives; if the endpoint reverted to the pre-#292 wiring
    // (`isTenantLocked ? null : GetService<IEnumerable<IFileParser>>()`, dropping the merge) the .ts file
    // would find no parser and its node would be CLEARED. Unlike the BuildRelayFileParsers helper tests above,
    // this one goes red on a regression in the ENDPOINT, not just in the helper.

    [Fact]
    public async Task RelayEndpoint_NotTenantLocked_MergesActivePluginParser_SoReindexKeepsTsNodes()
    {
        var (pm, synth, _, db, tsPath) = await SetupProjectWithSeededTsNodeAsync();

        // A fake plugin loader stands in for the real assembly loader so the test supplies a KNOWN active
        // plugin parser (the JS/TS one) without a plugin on disk or a live sidecar. This DI override is the
        // only substitution — everything else runs the real McpEndpoints.RelayAsync handler.
        RelayPluginLoader fakeLoader = (_, _) => new AssemblyPluginLoadResult(
            new List<IFileParser> { new FakeTsParser() },
            Array.Empty<IGraphPostProcessor>(),
            new List<AssemblyLoadContext>());

        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build()) // plugins enabled by default
            .AddLogging()
            .AddSingleton<IFileParser, RoslynAstParser>() // the host's DI parsers carry NO .ts parser (#292)
            .AddSingleton(fakeLoader)                      // the active plugin parser source
            .BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Headers["X-Project-Name"] = "P";
        // No AuthenticatedProjectName in Items => isTenantLocked = false: the trusted-local relay branch.
        context.Request.Body = new MemoryStream(
            Encoding.UTF8.GetBytes(ReindexCall(Path.GetFileName(tsPath))));

        await McpEndpoints.RelayAsync(context, pm, synth);

        // The endpoint merged the plugin's .ts parser into the reindex parser list, so the file was re-parsed
        // (its node kept) instead of cleared. Dropping the merge in the endpoint => no .ts parser => count 0.
        Assert.True(await NodeCountForFileAsync(db, tsPath) > 0);
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
