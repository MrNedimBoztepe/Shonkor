// Licensed to Shonkor under the MIT License.

using System.Text.Json;
using Shonkor.Core.Models;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;
using Shonkor.Infrastructure.Services.Mcp;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// The MCP tool contract must not lie by omission (#118, #119, #120).
/// <para>
/// Three separate ways it used to: a clamp that silently reduced a caller's bound; a failure surface that
/// only a human could read; and "no result" answers that could not be told apart from "your symbol doesn't
/// exist". Each one leaves an agent confidently wrong.
/// </para>
/// </summary>
public class McpToolContractTests
{
    private static async Task<McpRequestHandler> HandlerAsync(Func<SqliteGraphStorageProvider, Task>? seed = null)
    {
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_contract_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        var dbPath = Path.Combine(ws, "g.db");
        using (var storage = new SqliteGraphStorageProvider(dbPath))
        {
            await storage.InitializeAsync();
            if (seed != null) await seed(storage);
        }
        var registry = new
        {
            Organizations = Array.Empty<object>(),
            Users = Array.Empty<object>(),
            Projects = new[] { new { Name = "P", Path = ws, DatabasePath = dbPath, OrganizationId = "", RepositoryUrl = "", ApiKey = "" } },
            ActiveProjectName = "P"
        };
        File.WriteAllText(Path.Combine(ws, "projects.json"), JsonSerializer.Serialize(registry));
        return new McpRequestHandler(new ProjectManager(ws), new ContextCapsuleSynthesizer(), "P", lockToContextProject: true);
    }

    private static JsonElement Parse(string? json) => JsonDocument.Parse(json!).RootElement.Clone();

    private static string ToolCall(string name, object args) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name, arguments = args } });

    private static string ResultText(JsonElement root) =>
        root.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

    /// <summary>
    /// Seeds real classes so "the symbol exists" is distinguishable from "it doesn't". Each carries the
    /// shared token <c>gizmo</c> so one FTS query matches them all — <c>Widget3</c> and friends tokenize
    /// individually, so a bare "Widget" query would match nothing.
    /// </summary>
    private static Task Seed(SqliteGraphStorageProvider storage) => storage.UpsertNodesAsync(
        Enumerable.Range(0, 30).Select(i => new GraphNode
        {
            Id = $"c::Widget{i}", Name = $"Widget{i}", Type = "Class",
            FilePath = $"/x/Widget{i}.cs", Content = $"class Widget{i} {{ }} // gizmo"
        }));

    // ---- #119: a clamp that announces itself ---------------------------------------------------------

    [Fact]
    public async Task Clamp_WhenItBites_IsAnnounced()
    {
        var handler = await HandlerAsync(Seed);

        // 100000 is far past MaxResultLimit (100). The caller gets 30 hits and must not conclude that only
        // 30 exist BECAUSE its limit was honoured — it wasn't.
        var text = ResultText(Parse(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("search_graph", new { query = "gizmo", limit = 100_000 }))));

        Assert.Contains("limit clamped to 100", text);
        Assert.Contains("you requested 100000", text);
    }

    [Fact]
    public async Task Clamp_WhenItDoesNotBite_StaysSilent()
    {
        var handler = await HandlerAsync(Seed);

        // The fear that kept #119 unshipped was noise on every call. It is unfounded: the note only appears
        // when the caller actually overshoots, and this is the common path.
        var text = ResultText(Parse(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("search_graph", new { query = "gizmo", limit = 5 }))));

        Assert.DoesNotContain("clamped", text);
    }

    [Fact]
    public async Task Clamp_OnADefaultedValue_StaysSilent()
    {
        var handler = await HandlerAsync(Seed);

        // No limit supplied at all → the default applies. Announcing a "clamp" here would be noise about a
        // bound the caller never asked for.
        var text = ResultText(Parse(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("search_graph", new { query = "gizmo" }))));

        Assert.DoesNotContain("clamped", text);
    }

    [Fact]
    public async Task Clamp_OnJsonPayload_RidesInASecondBlock_LeavingTheJsonParseable()
    {
        var handler = await HandlerAsync(Seed);

        var root = Parse(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("search_graph", new { query = "gizmo", limit = 100_000, verbose = true })));
        var content = root.GetProperty("result").GetProperty("content");

        // content[0] must remain exactly the JSON callers already parse — announcing a cap must never
        // corrupt the payload it describes (the very bug #117 is about).
        var payload = content[0].GetProperty("text").GetString()!;
        using var doc = JsonDocument.Parse(payload);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);

        // ...and the note still reaches the model, in its own block.
        Assert.Equal(2, content.GetArrayLength());
        Assert.Contains("limit clamped to 100", content[1].GetProperty("text").GetString());
    }

    // ---- #118: a negative that is a FAILURE vs a negative that is an ANSWER --------------------------

    [Fact]
    public async Task SymbolThatDoesNotExist_IsAnError_NotABlandAnswer()
    {
        var handler = await HandlerAsync(Seed);

        // The agent invented (or mistyped) a symbol. That is a failed call, and it must be able to tell.
        var root = Parse(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("get_source", new { symbol = "Wdiget3" })));
        var result = root.GetProperty("result");

        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Equal(McpErrorCode.SymbolNotFound, result.GetProperty("_meta").GetProperty("code").GetString());
    }

    [Fact]
    public async Task SymbolThatExistsButHasNoDependents_IsAnAnswer_NotAnError()
    {
        var handler = await HandlerAsync(Seed);

        // "Nothing references Widget3" is a REAL, useful finding. Flagging it isError would teach the model
        // that a correct negative is a malfunction and invite pointless retries. This is the line #118 draws:
        // the subject exists → it is an answer; the subject does not → it is a failure.
        var root = Parse(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("references", new { symbol = "Widget3" })));
        var result = root.GetProperty("result");

        Assert.False(result.TryGetProperty("isError", out var err) && err.GetBoolean());
        Assert.Contains("Widget3", ResultText(root));
    }

    [Fact]
    public async Task FileThatIsNotIndexed_IsAnError()
    {
        var handler = await HandlerAsync(Seed);

        var root = Parse(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("outline", new { path = "Nowhere.cs" })));
        var result = root.GetProperty("result");

        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Equal(McpErrorCode.FileNotIndexed, result.GetProperty("_meta").GetProperty("code").GetString());
    }

    // ---- #120: stable, machine-readable failure identities -------------------------------------------

    [Fact]
    public async Task MissingRequiredArgument_Carries_MissingParameter_Code()
    {
        var handler = await HandlerAsync(Seed);

        var root = Parse(await handler.ProcessJsonRpcMessageAsync(ToolCall("search_graph", new { })));
        var error = root.GetProperty("error");

        Assert.Equal(-32602, error.GetProperty("code").GetInt32());
        Assert.Equal(McpErrorCode.MissingParameter, error.GetProperty("data").GetProperty("code").GetString());
    }

    [Fact]
    public async Task PathEscapingTheProjectRoot_Carries_PathOutsideRoot_Code()
    {
        var handler = await HandlerAsync(Seed);

        // TICKET-209 blocked this; #120 gives it a stable identity so a client can branch on the *reason*
        // rather than string-matching the prose (which is free to be reworded).
        var root = Parse(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("outline", new { path = "../../../../etc/passwd" })));
        var error = root.GetProperty("error");

        Assert.Equal(-32602, error.GetProperty("code").GetInt32());
        Assert.Equal(McpErrorCode.PathOutsideRoot, error.GetProperty("data").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ErrorMessages_StayHumanReadable_AlongsideTheCode()
    {
        var handler = await HandlerAsync(Seed);

        // The code is additive. The prose must not be sacrificed to it — a human reading a log still needs
        // to understand what happened without a lookup table.
        var root = Parse(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("get_source", new { symbol = "Nonexistent" })));
        var text = ResultText(root);

        Assert.Contains("Nonexistent", text);
        Assert.Contains("not in the graph", text);
        Assert.Contains("search_graph", text); // and it says how to recover
    }
}
