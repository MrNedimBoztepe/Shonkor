// Licensed to Shonkor under the MIT License.

using System.Text.Json;
using Shonkor.Core.Models;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// TICKET-210 protocol conformance: ping, parse/invalid-request errors, protocol-version negotiation,
/// notifications, tool execution errors surfaced as isError results, and the output clamps.
/// </summary>
public class McpProtocolConformanceTests
{
    private static string NewWorkspace()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_proto_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        return ws;
    }

    private static void WriteRegistry(string ws, string dbPath)
    {
        var registry = new
        {
            Organizations = Array.Empty<object>(),
            Users = Array.Empty<object>(),
            Projects = new[] { new { Name = "P", Path = ws, DatabasePath = dbPath, OrganizationId = "", RepositoryUrl = "", ApiKey = "" } },
            ActiveProjectName = "P"
        };
        File.WriteAllText(Path.Combine(ws, "projects.json"), JsonSerializer.Serialize(registry));
    }

    /// <summary>A handler over an initialized, optionally seeded graph.</summary>
    private static async Task<McpRequestHandler> HandlerAsync(Func<SqliteGraphStorageProvider, Task>? seed = null)
    {
        var ws = NewWorkspace();
        var dbPath = Path.Combine(ws, "g.db");
        using (var storage = new SqliteGraphStorageProvider(dbPath))
        {
            await storage.InitializeAsync();
            if (seed != null) await seed(storage);
        }
        WriteRegistry(ws, dbPath);
        return new McpRequestHandler(new ProjectManager(ws), new ContextCapsuleSynthesizer(), "P", lockToContextProject: true);
    }

    private static JsonElement Parse(string? json) => JsonDocument.Parse(json!).RootElement.Clone();

    private static string ToolCall(string name, object args) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name, arguments = args } });

    private static string ResultText(JsonElement root) =>
        root.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

    // ---- Envelope conformance (AC2) ------------------------------------------------------------------

    [Fact]
    public async Task Ping_RespondsWithEmptyResult()
    {
        var handler = await HandlerAsync();
        var root = Parse(await handler.ProcessJsonRpcMessageAsync(
            """{"jsonrpc":"2.0","id":7,"method":"ping"}"""));

        Assert.Equal(7, root.GetProperty("id").GetInt32());
        Assert.False(root.TryGetProperty("error", out _));
        Assert.Equal(JsonValueKind.Object, root.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task MalformedJson_Returns32700_WithNullId()
    {
        var handler = await HandlerAsync();
        var root = Parse(await handler.ProcessJsonRpcMessageAsync("{ this is not json "));

        Assert.Equal(-32700, root.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("id").ValueKind);
    }

    [Fact]
    public async Task NonObjectPayload_Returns32600_InvalidRequest()
    {
        var handler = await HandlerAsync();
        var root = Parse(await handler.ProcessJsonRpcMessageAsync("[1,2,3]"));

        Assert.Equal(-32600, root.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("id").ValueKind);
    }

    [Fact]
    public async Task Notification_HasNoIdKey_ProducesNoResponse()
    {
        var handler = await HandlerAsync();
        // A true notification: valid, complete, and expects nothing back (the relay turns this into 202).
        var response = await handler.ProcessJsonRpcMessageAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        Assert.Null(response);
    }

    [Theory]
    [InlineData("2025-06-18", "2025-06-18")]  // supported → honoured
    [InlineData("2024-11-05", "2024-11-05")]  // supported → honoured
    [InlineData("1999-01-01", "2025-06-18")]  // unsupported → answered with what we DO speak
    [InlineData("", "2025-06-18")]            // omitted → default
    public async Task Initialize_NegotiatesProtocolVersion(string requested, string expected)
    {
        var handler = await HandlerAsync();
        var payload = string.IsNullOrEmpty(requested)
            ? """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}"""
            : "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"" + requested + "\"}}";

        var root = Parse(await handler.ProcessJsonRpcMessageAsync(payload));
        Assert.Equal(expected, root.GetProperty("result").GetProperty("protocolVersion").GetString());
    }

    // ---- Tool execution errors as isError results (AC1) ----------------------------------------------

    [Fact]
    public async Task ToolExecutionFailure_IsReportedAsIsErrorResult_NotAProtocolError()
    {
        // Point the project's DatabasePath at a DIRECTORY so opening SQLite throws a non-argument error.
        var ws = NewWorkspace();
        var dbAsDirectory = Path.Combine(ws, "not-a-db");
        Directory.CreateDirectory(dbAsDirectory);
        WriteRegistry(ws, dbAsDirectory);
        var handler = new McpRequestHandler(new ProjectManager(ws), new ContextCapsuleSynthesizer(), "P", lockToContextProject: true);

        ExpectedError.Emit("get_stats over a DB path that is a directory — asserted below as an isError result, not thrown (#236)");
        var root = Parse(await handler.ProcessJsonRpcMessageAsync(ToolCall("get_stats", new { })));

        // The model must SEE the failure: it arrives in the result, flagged isError, not in the error channel.
        Assert.False(root.TryGetProperty("error", out _));
        Assert.True(root.GetProperty("result").GetProperty("isError").GetBoolean());

        var text = ResultText(root);
        Assert.Contains("get_stats", text);
        // TICKET-209 hygiene still holds: no raw exception message, hence no leaked path.
        Assert.DoesNotContain(dbAsDirectory, text);
    }

    [Fact]
    public async Task InvalidArgument_StaysAProtocolError_32602()
    {
        var handler = await HandlerAsync();
        // A required parameter is missing → parameter validation, which remains a JSON-RPC error.
        var root = Parse(await handler.ProcessJsonRpcMessageAsync(ToolCall("get_source", new { })));
        Assert.Equal(-32602, root.GetProperty("error").GetProperty("code").GetInt32());
    }

    // ---- Output clamps (AC3) -------------------------------------------------------------------------

    [Fact]
    public async Task SearchGraph_LimitIsClampedTo100()
    {
        var handler = await HandlerAsync(async storage =>
        {
            var nodes = Enumerable.Range(0, 150).Select(i => new GraphNode
            {
                Id = $"n::{i}",
                Name = $"Widget{i}",
                Type = "Class",
                Content = "class Widget"
            });
            await storage.UpsertNodesAsync(nodes);
        });

        var text = ResultText(Parse(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("search_graph", new { query = "Widget", limit = 100000 }))));

        var hits = text.Split('\n').Count(l => l.Contains("Widget", StringComparison.Ordinal));
        Assert.True(hits <= 100, $"limit must clamp to 100, got {hits} result lines");
    }

    [Fact]
    public async Task GetSource_HugeBody_IsCappedByDefault_WithHint()
    {
        var handler = await HandlerAsync(async storage =>
        {
            await storage.UpsertNodesAsync(new[]
            {
                new GraphNode { Id = "big::1", Name = "Huge", Type = "Class", Content = new string('x', 200_000) }
            });
        });

        var text = ResultText(Parse(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("get_source", new { symbol = "Huge" }))));

        Assert.True(text.Length < 200_000, "an unbounded body must not be returned in full by default");
        Assert.Contains("truncated to 32768 chars", text);
        Assert.Contains("raise maxChars", text);
    }

    /// <summary>
    /// #117: the verbose branch emits JSON, so its cap must be STRUCTURAL. TICKET-210 capped it by characters
    /// — honest about truncating, but it handed back a document the caller could not <c>JSON.parse</c>. A
    /// dangling brace is not an answer. Dropping whole nodes/edges keeps the payload valid and states the
    /// omission outright.
    /// </summary>
    [Fact]
    public async Task GetSubgraph_VerboseJson_StaysParseable_AndReportsWhatItDropped()
    {
        var handler = await HandlerAsync(async storage =>
        {
            var nodes = Enumerable.Range(0, 40).Select(i => new GraphNode
            {
                Id = $"v::{i}", Name = $"Node{i}", Type = "Class", FilePath = $"/x/Node{i}.cs", Content = "c"
            }).ToList();
            await storage.UpsertNodesAsync(nodes);
            await storage.UpsertEdgesAsync(Enumerable.Range(1, 39).Select(i =>
                new GraphEdge { SourceId = $"v::{i}", TargetId = "v::0", Relationship = "REFERENCES_TYPE" }));
        });

        var text = ResultText(Parse(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("get_subgraph", new { seeds = new[] { "v::0" }, hops = 1, verbose = true, maxChars = 600 }))));

        Assert.True(text.Length <= 600, $"verbose output must respect maxChars, got {text.Length} chars");

        // The whole point: it still parses. The old character cap produced a severed document.
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("truncated").GetBoolean(), "this subgraph cannot fit in 600 chars");
        Assert.Equal("maxChars", root.GetProperty("reason").GetString());

        var omitted = root.GetProperty("omitted");
        Assert.True(omitted.GetProperty("nodes").GetInt32() > 0, "omitted node count must be reported, not implied");

        // Referential integrity: every surviving edge points at surviving nodes. Dropping a node without its
        // edges would leave the caller holding edges into nodes that are not in the document.
        var keptIds = root.GetProperty("nodes").EnumerateArray()
            .Select(n => n.GetProperty("Id").GetString()).ToHashSet(StringComparer.Ordinal);
        foreach (var e in root.GetProperty("edges").EnumerateArray())
        {
            Assert.Contains(e.GetProperty("SourceId").GetString(), keptIds);
            Assert.Contains(e.GetProperty("TargetId").GetString(), keptIds);
        }
    }

    /// <summary>#117: an uncapped verbose call must report nothing omitted — the cap only speaks when it bites.</summary>
    [Fact]
    public async Task GetSubgraph_VerboseJson_WhenItFits_ReportsNoOmission()
    {
        var handler = await HandlerAsync(async storage =>
        {
            await storage.UpsertNodesAsync(new[]
            {
                new GraphNode { Id = "v::0", Name = "Root", Type = "Class", FilePath = "/x/Root.cs", Content = "c" },
                new GraphNode { Id = "v::1", Name = "Leaf", Type = "Class", FilePath = "/x/Leaf.cs", Content = "c" }
            });
            await storage.UpsertEdgesAsync(new[]
            {
                new GraphEdge { SourceId = "v::1", TargetId = "v::0", Relationship = "REFERENCES_TYPE" }
            });
        });

        var text = ResultText(Parse(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("get_subgraph", new { seeds = new[] { "v::0" }, hops = 1, verbose = true }))));

        using var doc = JsonDocument.Parse(text);
        Assert.False(doc.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(0, doc.RootElement.GetProperty("omitted").GetProperty("nodes").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("omitted").GetProperty("edges").GetInt32());
    }
}
