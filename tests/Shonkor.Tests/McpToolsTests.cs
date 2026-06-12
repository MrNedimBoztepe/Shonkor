// Licensed to Shonkor under the MIT License.

using System.Text.Json;

using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// Tests the AI-facing MCP tools: impact analysis (incoming references), reusable short node handles
/// that round-trip as seeds, AI summaries in the subgraph output, and the maxChars token budget.
/// </summary>
public class McpToolsTests
{
    private static async Task<(ProjectManager Pm, ContextCapsuleSynthesizer Synth, string Workspace)> SetupAsync()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_mcp_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        var dbPath = Path.Combine(ws, "g.db");

        var widgetId = Path.Combine(ws, "Widget.cs") + "::Widget";
        var gadgetId = Path.Combine(ws, "Gadget.cs") + "::Gadget";

        using (var storage = new SqliteGraphStorageProvider(dbPath))
        {
            await storage.InitializeAsync();
            await storage.UpsertNodesAsync(new[]
            {
                new GraphNode { Id = widgetId, Name = "Widget", Type = "Class", FilePath = Path.Combine(ws, "Widget.cs"), StartLine = 1, Content = "class Widget {}", Summary = "A reusable widget.", Embedding = new[] { 1f, 0f, 0f } },
                new GraphNode { Id = gadgetId, Name = "Gadget", Type = "Class", FilePath = Path.Combine(ws, "Gadget.cs"), StartLine = 1, Content = "class Gadget { Widget w; }", Summary = "Depends on Widget.", Embedding = new[] { 0f, 1f, 0f } },
                // Interaction nodes for get_open_threads (one open task, one done task, one open question).
                new GraphNode { Id = "task::open", Name = "Implement caching", Type = "Task", Content = "todo", Properties = new() { ["status"] = "Todo" } },
                new GraphNode { Id = "task::done", Name = "Old finished task", Type = "Task", Content = "done", Properties = new() { ["status"] = "Done" } },
                new GraphNode { Id = "task::donespace", Name = "Trimmed done task", Type = "Task", Content = "done", Properties = new() { ["status"] = "Done " } },
                new GraphNode { Id = "question::open", Name = "Why is X slow", Type = "Question", Content = "q", Properties = new() { ["status"] = "Open" } }
            });
            await storage.UpsertEdgesAsync(new[]
            {
                new GraphEdge { SourceId = gadgetId, TargetId = widgetId, Relationship = "REFERENCES_TYPE" }
            });
        }

        var registry = new
        {
            Organizations = Array.Empty<object>(),
            Users = Array.Empty<object>(),
            Projects = new[] { new { Name = "P", Path = ws, DatabasePath = dbPath, OrganizationId = "", RepositoryUrl = "", ApiKey = "" } },
            ActiveProjectName = "P"
        };
        File.WriteAllText(Path.Combine(ws, "projects.json"), JsonSerializer.Serialize(registry));

        return (new ProjectManager(ws), new ContextCapsuleSynthesizer(), ws);
    }

    private static string ToolCall(string tool, object args) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments = args } });

    private static string TextOf(string? response)
    {
        using var doc = JsonDocument.Parse(response!);
        return doc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
    }

    [Fact]
    public async Task ImpactOf_ListsDependentsWithRelationAndSummary()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("impact_of", new { symbol = "Widget" })));

        Assert.Contains("Gadget", text);             // the dependent is listed
        Assert.Contains("REFERENCES_TYPE", text);    // grouped by relation
        Assert.Contains("Depends on Widget.", text); // the dependent's AI summary is included
    }

    [Fact]
    public async Task ImpactOf_NoDependents_ReportsSafe()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        // Gadget is referenced by nothing -> safe to change.
        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("impact_of", new { symbol = "Gadget" })));
        Assert.Contains("Nothing references", text);
    }

    [Fact]
    public async Task GetSubgraph_EmitsHandlesAndSummaries_AndHandleRoundTripsAsSeed()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        // Seed using a short "@/…" handle instead of the absolute id.
        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("get_subgraph", new { seeds = new[] { "@/Widget.cs::Widget" }, hops = 1 })));

        Assert.Contains("@/Widget.cs::Widget", text);  // handle emitted (and was accepted as a seed)
        Assert.Contains("@/Gadget.cs::Gadget", text);  // neighbour handle
        Assert.Contains("A reusable widget.", text);   // node summary included
        Assert.Contains("--REFERENCES_TYPE-->", text); // edge using handles
    }

    [Fact]
    public async Task GetSubgraph_MaxChars_TruncatesOutput()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("get_subgraph", new { seeds = new[] { "@/Widget.cs::Widget" }, hops = 1, maxChars = 40 })));

        Assert.Contains("truncated", text);
        Assert.True(text.Length < 120);
    }

    [Fact]
    public async Task ToolsList_AdvertisesImpactOf()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        var res = await handler.ProcessJsonRpcMessageAsync(
            JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/list" }));

        Assert.Contains("impact_of", res);
    }

    [Fact]
    public async Task VerifyExists_ConfirmsAndDenies()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        var yes = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("verify_exists", new { symbol = "Widget" })));
        Assert.StartsWith("YES", yes);
        Assert.Contains("Widget", yes);

        var no = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("verify_exists", new { symbol = "TotallyMadeUpType" })));
        Assert.StartsWith("NO", no);
    }

    [Fact]
    public async Task GetSource_ReturnsExactBody_WithLocation()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("get_source", new { symbol = "Widget" })));

        Assert.Contains("Widget (Class)", text);     // header with name + type
        Assert.Contains("Widget.cs:1", text);         // file:line location
        Assert.Contains("class Widget {}", text);     // the exact stored body
    }

    [Fact]
    public async Task FindUsages_ListsCallSites_WithSnippet()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        // Gadget --REFERENCES_TYPE--> Widget, and Gadget's body mentions Widget.
        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("find_usages", new { symbol = "Widget" })));

        Assert.Contains("usage(s) of 'Widget'", text);
        Assert.Contains("REFERENCES_TYPE", text);
        Assert.Contains("Gadget", text);
        Assert.Contains("class Gadget { Widget w; }", text); // the grep-like usage snippet

        // Widget references nothing, so it has no usages reported for Gadget.
        var none = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("find_usages", new { symbol = "Gadget" })));
        Assert.Contains("No usages", none);
    }

    [Fact]
    public async Task ReindexFile_RefreshesGraph_AndDegradesWithoutParsers()
    {
        var (pm, synth, ws) = await SetupAsync();

        // Without parsers (e.g. SaaS relay) the tool degrades gracefully.
        var noParsers = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);
        var unavailable = TextOf(await noParsers.ProcessJsonRpcMessageAsync(
            ToolCall("reindex_file", new { path = "Foo.cs" })));
        Assert.Contains("unavailable", unavailable);

        // With parsers + a real file, the file is indexed and immediately queryable via get_source.
        await File.WriteAllTextAsync(Path.Combine(ws, "Foo.cs"), "namespace D; public class Foo { }");
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true,
            fileParsers: new IFileParser[] { new RoslynAstParser() });

        var reindexed = TextOf(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("reindex_file", new { path = "@/Foo.cs" })));
        Assert.Contains("Reindexed", reindexed);

        var src = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("get_source", new { symbol = "Foo" })));
        Assert.Contains("Foo", src);
    }

    [Fact]
    public async Task DependsOn_ListsOutgoingReferences()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        // Gadget --REFERENCES_TYPE--> Widget, so Gadget depends on Widget.
        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("depends_on", new { symbol = "Gadget" })));
        Assert.Contains("Widget", text);
        Assert.Contains("REFERENCES_TYPE", text);
        Assert.Contains("A reusable widget.", text); // dependency's AI summary

        // Widget points at nothing -> self-contained.
        var leaf = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("depends_on", new { symbol = "Widget" })));
        Assert.Contains("self-contained", leaf);
    }

    [Fact]
    public async Task FindPath_ReturnsChainWithRealEdgeDirection()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        // Edge is Gadget --REFERENCES_TYPE--> Widget.
        var forward = TextOf(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("find_path", new { from = "Gadget", to = "Widget" })));
        Assert.Contains("Gadget --REFERENCES_TYPE--> Widget", forward);

        // Reverse direction must render the arrow the other way (traversed against the edge).
        var backward = TextOf(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("find_path", new { from = "Widget", to = "Gadget" })));
        Assert.Contains("Widget <--REFERENCES_TYPE-- Gadget", backward);
    }

    [Fact]
    public async Task FindPath_NoConnection_ReportsClearly()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        // The open Task node shares no edge with Widget.
        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("find_path", new { from = "Widget", to = "Implement caching" })));
        Assert.Contains("No path", text);
    }

    [Fact]
    public async Task FindPath_MultiHop_ReconstructsFullChainInOrder()
    {
        // Cog --REFERENCES_TYPE--> Gadget --REFERENCES_TYPE--> Widget (an isolated 3-node chain,
        // so the shared SetupAsync's assertions stay untouched).
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_chain_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        var dbPath = Path.Combine(ws, "g.db");
        string Nid(string n) => Path.Combine(ws, $"{n}.cs") + $"::{n}";

        using (var storage = new SqliteGraphStorageProvider(dbPath))
        {
            await storage.InitializeAsync();
            await storage.UpsertNodesAsync(new[]
            {
                new GraphNode { Id = Nid("Widget"), Name = "Widget", Type = "Class", FilePath = Path.Combine(ws, "Widget.cs") },
                new GraphNode { Id = Nid("Gadget"), Name = "Gadget", Type = "Class", FilePath = Path.Combine(ws, "Gadget.cs") },
                new GraphNode { Id = Nid("Cog"),    Name = "Cog",    Type = "Class", FilePath = Path.Combine(ws, "Cog.cs") }
            });
            await storage.UpsertEdgesAsync(new[]
            {
                new GraphEdge { SourceId = Nid("Cog"),    TargetId = Nid("Gadget"), Relationship = "REFERENCES_TYPE" },
                new GraphEdge { SourceId = Nid("Gadget"), TargetId = Nid("Widget"), Relationship = "REFERENCES_TYPE" }
            });
        }

        var registry = new
        {
            Organizations = Array.Empty<object>(),
            Users = Array.Empty<object>(),
            Projects = new[] { new { Name = "P", Path = ws, DatabasePath = dbPath, OrganizationId = "", RepositoryUrl = "", ApiKey = "" } },
            ActiveProjectName = "P"
        };
        File.WriteAllText(Path.Combine(ws, "projects.json"), JsonSerializer.Serialize(registry));

        var handler = new McpRequestHandler(new ProjectManager(ws), new ContextCapsuleSynthesizer(), "P", lockToContextProject: true);

        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("find_path", new { from = "Cog", to = "Widget" })));

        // The full two-hop chain, in source->target order, each arrow forward.
        Assert.Contains("Cog --REFERENCES_TYPE--> Gadget --REFERENCES_TYPE--> Widget", text);
        Assert.Contains("2 hop(s)", text);
    }

    /// <summary>Returns a fixed embedding regardless of input — lets the test pin which seeded node ranks first.</summary>
    private sealed class StubEmbeddingService : IEmbeddingService
    {
        private readonly float[] _vector;
        public StubEmbeddingService(float[] vector) => _vector = vector;
        public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default) => Task.FromResult(_vector);
    }

    [Fact]
    public async Task SearchSemantic_RanksByVectorSimilarity_WithHandleAndSummary()
    {
        var (pm, synth, _) = await SetupAsync();
        // Query vector aligned with Widget's embedding [1,0,0] -> Widget must rank first.
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true,
            embeddingService: new StubEmbeddingService(new[] { 1f, 0f, 0f }));

        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("search_semantic", new { query = "a reusable component" })));

        Assert.Contains("Widget", text);
        Assert.Contains("A reusable widget.", text);          // AI summary included
        Assert.Contains("@/Widget.cs::Widget", text);          // short handle, not the absolute id
        Assert.True(text.IndexOf("Widget", StringComparison.Ordinal)
                  < text.IndexOf("Gadget", StringComparison.Ordinal)); // ranked above the orthogonal node
    }

    [Fact]
    public async Task SearchSemantic_WithoutEmbeddingBackend_DegradesGracefully()
    {
        var (pm, synth, _) = await SetupAsync();
        // No embedding service (the stdio/CLI case) -> must not throw, returns a clear message.
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("search_semantic", new { query = "anything" })));

        Assert.Contains("unavailable", text);
        Assert.Contains("search_graph", text); // points the caller at the keyword fallback
    }

    [Fact]
    public async Task GetOpenThreads_ListsOpen_ExcludesClosed()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("get_open_threads", new { })));

        Assert.Contains("Implement caching", text);   // open task
        Assert.Contains("Why is X slow", text);        // open question
        Assert.DoesNotContain("Old finished task", text); // Done -> excluded
        Assert.DoesNotContain("Trimmed done task", text); // "Done " (trailing space) -> trimmed, still excluded
    }

    [Fact]
    public async Task FindPath_TolerantOfNumericStringArg()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        // maxHops as a JSON string "1" must not blow up with an internal error — ReadInt parses it.
        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("find_path", new { from = "Gadget", to = "Widget", maxHops = "1" })));

        Assert.Contains("Gadget --REFERENCES_TYPE--> Widget", text);
    }
}
