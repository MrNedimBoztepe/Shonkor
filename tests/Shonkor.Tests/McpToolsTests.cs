// Licensed to Shonkor under the MIT License.

using System.Text.Json;

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
                new GraphNode { Id = widgetId, Name = "Widget", Type = "Class", FilePath = Path.Combine(ws, "Widget.cs"), StartLine = 1, Content = "class Widget {}", Summary = "A reusable widget." },
                new GraphNode { Id = gadgetId, Name = "Gadget", Type = "Class", FilePath = Path.Combine(ws, "Gadget.cs"), StartLine = 1, Content = "class Gadget { Widget w; }", Summary = "Depends on Widget." },
                // Interaction nodes for get_open_threads (one open task, one done task, one open question).
                new GraphNode { Id = "task::open", Name = "Implement caching", Type = "Task", Content = "todo", Properties = new() { ["status"] = "Todo" } },
                new GraphNode { Id = "task::done", Name = "Old finished task", Type = "Task", Content = "done", Properties = new() { ["status"] = "Done" } },
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
    public async Task GetOpenThreads_ListsOpen_ExcludesClosed()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("get_open_threads", new { })));

        Assert.Contains("Implement caching", text);   // open task
        Assert.Contains("Why is X slow", text);        // open question
        Assert.DoesNotContain("Old finished task", text); // Done -> excluded
    }
}
