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

    [Fact]
    public async Task GetSubgraph_VerboseJson_IsCapped_NoLongerBypassesTheLimit()
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

        // The verbose branch previously ignored maxChars entirely.
        var text = ResultText(Parse(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("get_subgraph", new { seeds = new[] { "v::0" }, hops = 1, verbose = true, maxChars = 200 }))));

        Assert.Contains("truncated to 200 chars", text);
        Assert.True(text.Length < 400, $"verbose output must respect maxChars, got {text.Length} chars");
    }
}
