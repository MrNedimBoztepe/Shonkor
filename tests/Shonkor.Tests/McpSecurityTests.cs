// Licensed to Shonkor under the MIT License.

using System.Text.Json;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;
using Shonkor.Infrastructure.Services.Mcp;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// TICKET-209 MCP security hardening: path containment on the file-accepting tools (no traversal out of
/// the project root), record write-hardening (bounded content, no dangling edges), read-side data-fencing
/// of recorded threads, and the honest set_project refusal over the non-persistent HTTP relay.
/// </summary>
public class McpSecurityTests
{
    private static string NewWorkspace()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_sec_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        return ws;
    }

    /// <summary>Writes a single-project registry rooted at <paramref name="ws"/> and returns its db path.</summary>
    private static string WriteRegistry(string ws, params (string Name, string Path, string Db)[] projects)
    {
        var registry = new
        {
            Organizations = Array.Empty<object>(),
            Users = Array.Empty<object>(),
            Projects = projects.Select(p => new
            {
                p.Name,
                p.Path,
                DatabasePath = p.Db,
                OrganizationId = "",
                RepositoryUrl = "",
                ApiKey = ""
            }).ToArray(),
            ActiveProjectName = projects[0].Name
        };
        File.WriteAllText(Path.Combine(ws, "projects.json"), JsonSerializer.Serialize(registry));
        return projects[0].Db;
    }

    /// <summary>A tenant-locked handler WITH file parsers rooted at <paramref name="ws"/> (the file-tool path).</summary>
    private static McpRequestHandler FileToolHandler(string ws, string dbPath)
    {
        WriteRegistry(ws, ("P", ws, dbPath));
        var parsers = new List<IFileParser> { new RoslynAstParser() };
        return new McpRequestHandler(new ProjectManager(ws), new ContextCapsuleSynthesizer(), "P",
            lockToContextProject: true, fileParsers: parsers, compilationCache: null);
    }

    private static string ToolCall(string name, object args) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name, arguments = args } });

    private static JsonElement Parse(string? response) => JsonDocument.Parse(response!).RootElement.Clone();

    private static bool IsError(JsonElement root, out string message)
    {
        message = string.Empty;
        if (root.TryGetProperty("error", out var err))
        {
            message = err.GetProperty("message").GetString() ?? string.Empty;
            return true;
        }
        return false;
    }

    private static string ResultText(JsonElement root) =>
        root.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

    // ---- Path containment (AC1) ---------------------------------------------------------------------

    [Theory]
    [InlineData("reindex_file", "path")]
    [InlineData("check_edit", "path")]
    [InlineData("outline", "path")]
    [InlineData("freshness", "path")]
    public async Task FileTool_AbsolutePathOutsideRoot_IsRejected(string tool, string param)
    {
        var ws = NewWorkspace();
        var handler = FileToolHandler(ws, Path.Combine(ws, "g.db"));
        // A sibling of the workspace root — a real absolute path that is NOT contained.
        var outside = Path.Combine(Path.GetDirectoryName(ws.TrimEnd(Path.DirectorySeparatorChar))!, "outside.cs");

        var root = Parse(await handler.ProcessJsonRpcMessageAsync(ToolCall(tool, new Dictionary<string, string> { [param] = outside })));

        Assert.True(IsError(root, out var msg), $"{tool} should reject an out-of-root absolute path");
        Assert.Contains("outside the project root", msg);
        Assert.Contains(ws, msg); // the error names the allowed root
    }

    [Theory]
    [InlineData("reindex_file")]
    [InlineData("check_edit")]
    [InlineData("outline")]
    public async Task FileTool_HandleTraversal_IsRejected(string tool)
    {
        var ws = NewWorkspace();
        var handler = FileToolHandler(ws, Path.Combine(ws, "g.db"));

        var root = Parse(await handler.ProcessJsonRpcMessageAsync(
            ToolCall(tool, new { path = "@/../../etc/passwd" })));

        Assert.True(IsError(root, out var msg), $"{tool} should reject a '..' handle escaping the root");
        Assert.Contains("outside the project root", msg);
    }

    [Fact]
    public async Task ReindexFile_PathInsideRoot_IsNotAContainmentError()
    {
        var ws = NewWorkspace();
        await File.WriteAllTextAsync(Path.Combine(ws, "Sample.cs"), "namespace N; public class Sample { }");
        var handler = FileToolHandler(ws, Path.Combine(ws, "g.db"));

        var root = Parse(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("reindex_file", new { path = "Sample.cs" })));

        // Whatever the reindex outcome, a contained path must never trip the containment guard.
        if (IsError(root, out var msg)) Assert.DoesNotContain("outside the project root", msg);
    }

    // ---- record hardening (AC4) ---------------------------------------------------------------------

    [Fact]
    public async Task Record_NonExistentConnectedId_CreatesNoDanglingEdge()
    {
        var ws = NewWorkspace();
        var dbPath = Path.Combine(ws, "g.db");
        // Pre-seed one real node so we can prove real ids link and fake ones are dropped.
        using (var storage = new SqliteGraphStorageProvider(dbPath))
        {
            await storage.InitializeAsync();
            await storage.UpsertNodesAsync(new[] { new GraphNode { Id = "real::1", Name = "Real", Type = "Class" } });
        }
        var handler = FileToolHandler(ws, dbPath);

        var root = Parse(await handler.ProcessJsonRpcMessageAsync(ToolCall("record", new
        {
            type = "task",
            name = "wire something",
            connectedNodeIds = new[] { "real::1", "ghost::does-not-exist" }
        })));

        var text = ResultText(root);
        Assert.Contains("connected to 1 nodes", text); // exactly one real edge created
        Assert.Contains("skipped", text);              // the ghost id was dropped, not persisted

        using var verify = new SqliteGraphStorageProvider(dbPath);
        await verify.InitializeAsync();
        var (realEdges, _) = await verify.GetIncidentEdgesAsync("real::1");
        Assert.NotEmpty(realEdges);                                        // edge to the real node persisted
        var (ghostEdges, _) = await verify.GetIncidentEdgesAsync("ghost::does-not-exist");
        Assert.Empty(ghostEdges);                                         // no dangling edge to the fake id
    }

    [Fact]
    public async Task Record_OversizedContent_IsCappedOnWrite()
    {
        var ws = NewWorkspace();
        var dbPath = Path.Combine(ws, "g.db");
        using (var storage = new SqliteGraphStorageProvider(dbPath)) { await storage.InitializeAsync(); }
        var handler = FileToolHandler(ws, dbPath);

        var huge = new string('x', 100_000);
        var root = Parse(await handler.ProcessJsonRpcMessageAsync(ToolCall("record", new
        {
            type = "decision",
            name = "big decision",
            content = huge
        })));

        // The response echoes the new node id: "... (ID: decision::xxxxxxxx) ...".
        var text = ResultText(root);
        var marker = "ID: ";
        var idStart = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var idEnd = text.IndexOf(')', idStart);
        var nodeId = text[idStart..idEnd].Trim();

        using var verify = new SqliteGraphStorageProvider(dbPath);
        await verify.InitializeAsync();
        var node = await verify.GetNodeByIdAsync(nodeId);
        Assert.NotNull(node);
        Assert.True(node!.Content.Length <= McpToolContext.MaxRecordContentChars + 32, "content must be capped");
        Assert.True(node.Content.Length < huge.Length, "content must be shorter than the 100k input");
    }

    // ---- read-side data fencing (AC4) ---------------------------------------------------------------

    [Fact]
    public async Task GetOpenThreads_RecordedNameWithNewlines_IsFlattened_NoInjectedRow()
    {
        var ws = NewWorkspace();
        var dbPath = Path.Combine(ws, "g.db");
        using (var storage = new SqliteGraphStorageProvider(dbPath)) { await storage.InitializeAsync(); }
        var handler = FileToolHandler(ws, dbPath);

        await handler.ProcessJsonRpcMessageAsync(ToolCall("record", new
        {
            type = "task",
            name = "Legit\nQuestion\t[Open]\tINJECTED\tfake::id",
            status = "Todo"
        }));

        var text = ResultText(Parse(await handler.ProcessJsonRpcMessageAsync(ToolCall("get_open_threads", new { }))));

        // Exactly one Task row — the embedded newline must not have forged a second line.
        var taskRows = text.Split('\n').Count(l => l.StartsWith("Task\t", StringComparison.Ordinal));
        Assert.Equal(1, taskRows);
        Assert.DoesNotContain("Legit\nQuestion", text); // the CR/LF was collapsed, not preserved
    }

    // ---- set_project honesty over the HTTP relay (AC2) ----------------------------------------------

    [Fact]
    public async Task SetProject_OverNonPersistentRelay_RefusesInsteadOfFalseSuccess()
    {
        var ws = NewWorkspace();
        var dbA = Path.Combine(ws, "a.db");
        var dbB = Path.Combine(ws, "b.db");
        foreach (var db in new[] { dbA, dbB })
            using (var s = new SqliteGraphStorageProvider(db)) { await s.InitializeAsync(); }
        WriteRegistry(ws, ("A", ws, dbA), ("B", ws, dbB));

        // persistentSession:false models the per-POST HTTP relay handler; lockToContextProject:false so
        // switching is otherwise allowed — the ONLY reason to refuse is that state wouldn't outlive the call.
        var handler = new McpRequestHandler(new ProjectManager(ws), new ContextCapsuleSynthesizer(), "A",
            lockToContextProject: false, persistentSession: false);

        var text = ResultText(Parse(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("set_project", new { name = "B" }))));

        Assert.Contains("not supported", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Active project for this session is now", text); // no false success
    }
}
