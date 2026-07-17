// Licensed to Shonkor under the MIT License.

using System.Text.Json;

using Shonkor.Core.Models;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// Regression tests for #288: a JS module queried by its bare stem ('moco-api') resolved to the parser's
/// per-file JSComponent node — which by construction carries no inbound IMPORTS (those land on the File
/// node) — and every impact/safety tool reported a false all-clear. Covers Option 1 (stem → File-node
/// redirect so the bare stem and the '.js' File-node query answer identically), Option 3 (no "safe to
/// change in isolation" for a node type that structurally cannot receive the edge), the edit_plan
/// containment-edge filter, and the get_subgraph both-separator handle contract.
/// </summary>
public class JsStemResolutionTests
{
    // The graph the JS parser + scanner produce for a JS project: a File node per file (id = full path),
    // a JSComponent per file (id = '{path}::{stem}'), a File --CONTAINS--> JSComponent edge, and every
    // IMPORTS edge pointing at the imported file's File node (never its component).
    private static async Task<(McpRequestHandler Handler, string Workspace)> SetupAsync()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_jsstem_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(ws, "src", "background"));
        var dbPath = Path.Combine(ws, "g.db");

        string FileId(string rel) => Path.Combine(ws, rel.Replace('/', Path.DirectorySeparatorChar));
        string CompId(string rel) => FileId(rel) + "::" + Path.GetFileNameWithoutExtension(rel);

        GraphNode FileNode(string rel) => new()
        {
            Id = FileId(rel),
            Name = Path.GetFileName(rel),
            Type = "File",
            FilePath = FileId(rel),
            Content = $"// {rel}"
        };
        GraphNode Comp(string rel) => new()
        {
            Id = CompId(rel),
            Name = Path.GetFileNameWithoutExtension(rel),
            Type = "JSComponent",
            FilePath = FileId(rel)
        };
        GraphEdge Contains(string rel) => new() { SourceId = FileId(rel), TargetId = CompId(rel), Relationship = "CONTAINS" };
        // An importer's component IMPORTS the target FILE node (as the parser emits it).
        GraphEdge Imports(string importerRel, string targetRel) => new()
        {
            SourceId = CompId(importerRel),
            TargetId = FileId(targetRel),
            Relationship = "IMPORTS",
            Provenance = Provenance.Inferred
        };

        var mocoApi = "src/background/moco-api.js";
        var importers = new[] { "src/background/background.js", "src/background/sync-engine.js", "src/background/jira-mapper.js" };

        using (var storage = new SqliteGraphStorageProvider(dbPath))
        {
            await storage.InitializeAsync();

            var nodes = new List<GraphNode> { FileNode(mocoApi), Comp(mocoApi) };
            var edges = new List<GraphEdge> { Contains(mocoApi) };
            foreach (var imp in importers)
            {
                nodes.Add(FileNode(imp));
                nodes.Add(Comp(imp));
                edges.Add(Contains(imp));
                edges.Add(Imports(imp, mocoApi));
            }

            // A node whose ONLY incoming edge is structural containment — to prove edit_plan never lists a
            // CONTAINS edge as a reference site (independent of the JS redirect).
            var lonely = Path.Combine(ws, "Lonely.cs");
            nodes.Add(new GraphNode { Id = lonely, Name = "Lonely.cs", Type = "File", FilePath = lonely });
            nodes.Add(new GraphNode { Id = lonely + "::Lonely", Name = "Lonely", Type = "Class", FilePath = lonely, StartLine = 1, Content = "class Lonely {}" });
            edges.Add(new GraphEdge { SourceId = lonely, TargetId = lonely + "::Lonely", Relationship = "CONTAINS" });

            // A JSComponent with NO backing File node — the pathological case where the Option 1 redirect
            // cannot fire, so Option 3's messaging must still refuse a false all-clear.
            nodes.Add(new GraphNode { Id = Path.Combine(ws, "orphan.js") + "::orphan", Name = "orphan", Type = "JSComponent", FilePath = Path.Combine(ws, "orphan.js") });

            await storage.UpsertNodesAsync(nodes);
            await storage.UpsertEdgesAsync(edges);
        }

        var registry = new
        {
            Organizations = Array.Empty<object>(),
            Users = Array.Empty<object>(),
            Projects = new[] { new { Name = "P", Path = ws, DatabasePath = dbPath, OrganizationId = "", RepositoryUrl = "", ApiKey = "" } },
            ActiveProjectName = "P"
        };
        File.WriteAllText(Path.Combine(ws, "projects.json"), JsonSerializer.Serialize(registry));

        return (new McpRequestHandler(new ProjectManager(ws), new ContextCapsuleSynthesizer(), "P", lockToContextProject: true), ws);
    }

    private static string ToolCall(string tool, object args) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments = args } });

    private static string TextOf(string? response)
    {
        using var doc = JsonDocument.Parse(response!);
        return doc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
    }

    // ---- AC1: bare stem == '.js' File-node query, across references / find_usages / blast_radius --------

    [Fact]
    public async Task References_BareStem_ListsSameImportersAs_JsFileNodeQuery()
    {
        var (handler, _) = await SetupAsync();

        var byStem = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("references", new { symbol = "moco-api", direction = "used_by" })));
        var byFile = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("references", new { symbol = "moco-api.js", direction = "used_by" })));

        foreach (var importer in new[] { "background", "sync-engine", "jira-mapper" })
        {
            Assert.Contains(importer, byStem);
            Assert.Contains(importer, byFile);
        }
        Assert.Contains("IMPORTS", byStem);
        // The bare stem no longer reports a false all-clear.
        Assert.DoesNotContain("safe to change in isolation", byStem);
    }

    [Fact]
    public async Task FindUsages_BareStem_MatchesJsFileNodeQuery()
    {
        var (handler, _) = await SetupAsync();

        var byStem = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("find_usages", new { symbol = "moco-api" })));
        var byFile = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("find_usages", new { symbol = "moco-api.js" })));

        Assert.Contains("usage(s) of 'moco-api.js'", byStem); // resolved onto the File node
        Assert.DoesNotContain("No usages", byStem);
        foreach (var importer in new[] { "background", "sync-engine", "jira-mapper" })
        {
            Assert.Contains(importer, byStem);
            Assert.Contains(importer, byFile);
        }
    }

    [Fact]
    public async Task BlastRadius_BareStem_ReportsImporters_NotEmpty()
    {
        var (handler, _) = await SetupAsync();

        JsonElement Affected(string? resp)
        {
            using var doc = JsonDocument.Parse(resp!);
            var text = doc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
            return JsonDocument.Parse(text).RootElement.Clone();
        }

        var stem = Affected(await handler.ProcessJsonRpcMessageAsync(ToolCall("blast_radius", new { nodeOrFile = "moco-api" })));
        var file = Affected(await handler.ProcessJsonRpcMessageAsync(ToolCall("blast_radius", new { nodeOrFile = "moco-api.js" })));

        var stemNames = stem.GetProperty("affected").EnumerateArray().Select(a => a.GetProperty("name").GetString()).ToHashSet();
        var fileNames = file.GetProperty("affected").EnumerateArray().Select(a => a.GetProperty("name").GetString()).ToHashSet();

        Assert.Contains("background", stemNames);
        Assert.Contains("sync-engine", stemNames);
        Assert.Contains("jira-mapper", stemNames);
        Assert.Equal(fileNames, stemNames); // the bare stem answers identically to the File-node query
    }

    // ---- AC3: edit_plan lists importers, and never a structural containment edge -------------------------

    [Fact]
    public async Task EditPlan_BareStem_ListsImporters_AsReferenceSites()
    {
        var (handler, _) = await SetupAsync();

        var plan = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("edit_plan", new { symbol = "moco-api" })));

        Assert.Contains("Edit plan", plan);
        Assert.Contains("IMPORTS", plan);
        foreach (var importer in new[] { "background", "sync-engine", "jira-mapper" })
            Assert.Contains(importer, plan);
        Assert.DoesNotContain("safe to change in isolation", plan);
    }

    [Fact]
    public async Task EditPlan_NeverLists_StructuralContainmentEdge_AsReferenceSite()
    {
        var (handler, _) = await SetupAsync();

        // 'Lonely' is reached only by an incoming CONTAINS edge. Pre-#288 edit_plan listed it as a
        // reference site ("Update 1 reference site … (CONTAINS)"); it must now report none.
        var plan = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("edit_plan", new { symbol = "Lonely" })));

        Assert.Contains("Edit plan", plan);
        Assert.DoesNotContain("CONTAINS", plan);
        Assert.DoesNotContain("reference site(s)", plan);
        Assert.Contains("No reference sites", plan);
    }

    // ---- AC2 / Option 3: no false all-clear for a node that cannot carry the edge -----------------------

    [Fact]
    public async Task ImpactTools_JsComponentWithoutFileNode_PointAtTheFile_NotSafeToChange()
    {
        var (handler, _) = await SetupAsync();

        // 'orphan' resolves to a JSComponent with no backing File node, so the redirect cannot fire. Every
        // impact/safety tool must refuse the "safe to change in isolation" all-clear and point at the file.
        var refs = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("references", new { symbol = "orphan", direction = "used_by" })));
        Assert.DoesNotContain("safe to change in isolation", refs);
        Assert.Contains("JSComponent", refs);
        Assert.Contains("orphan.js", refs);

        var blast = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("references", new { symbol = "orphan", direction = "used_by", depth = 3 })));
        Assert.DoesNotContain("safe to change in isolation", blast);
        Assert.Contains("orphan.js", blast);

        var plan = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("edit_plan", new { symbol = "orphan" })));
        Assert.DoesNotContain("safe to change in isolation", plan);
        Assert.Contains("orphan.js", plan);
    }

    // ---- AC4: get_subgraph handle accepts both '/' and '\' separators for the same logical path ----------

    [Fact]
    public async Task GetSubgraph_Handle_AcceptsBothSlashDirections_ForTheSameLogicalPath()
    {
        var (handler, _) = await SetupAsync();

        var forward = TextOf(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("get_subgraph", new { seeds = new[] { "@/src/background/moco-api.js" }, hops = 1 })));
        var backslash = TextOf(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("get_subgraph", new { seeds = new[] { "@/src\\background\\moco-api.js" }, hops = 1 })));

        // Both forms resolve to the same seed, so neither silently returns an empty graph.
        Assert.Contains("moco-api.js", forward);
        Assert.Contains("moco-api.js", backslash);
        Assert.Equal(forward, backslash);
        Assert.DoesNotContain("NODES (0)", forward);
    }
}
