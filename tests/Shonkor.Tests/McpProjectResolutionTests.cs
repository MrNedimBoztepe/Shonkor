// Licensed to Shonkor under the MIT License.

using System.Text.Json;

using Shonkor.Core.Models;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// Regression tests for BUG-003 (an unknown/deleted project name must error instead of silently
/// falling back to the shared active project) and BUG-006 (set_project must refuse over the
/// per-request HTTP relay instead of claiming a switch that dies with the request).
/// </summary>
public class McpProjectResolutionTests
{
    private static async Task<(ProjectManager Pm, ContextCapsuleSynthesizer Synth, string Workspace)> SetupAsync()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_projres_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        var dbPath = Path.Combine(ws, "g.db");

        using (var storage = new SqliteGraphStorageProvider(dbPath))
        {
            await storage.InitializeAsync();
            await storage.UpsertNodesAsync(new[]
            {
                new GraphNode { Id = Path.Combine(ws, "Widget.cs") + "::Widget", Name = "Widget", Type = "Class", FilePath = Path.Combine(ws, "Widget.cs"), StartLine = 1, Content = "class Widget {}" }
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

    private static (int Code, string Message) ErrorOf(string? response)
    {
        using var doc = JsonDocument.Parse(response!);
        var error = doc.RootElement.GetProperty("error");
        return (error.GetProperty("code").GetInt32(), error.GetProperty("message").GetString()!);
    }

    [Fact]
    public async Task GetStorageProviderAsync_UnknownProject_Throws()
    {
        var (pm, _, _) = await SetupAsync();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => pm.GetStorageProviderAsync("DoesNotExist"));
    }

    [Fact]
    public async Task ToolCall_UnknownProjectName_ReturnsInvalidParams_NoActiveProjectFallback()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth);

        var response = await handler.ProcessJsonRpcMessageAsync(
            ToolCall("references", new { symbol = "Widget", projectName = "DoesNotExist" }));

        var (code, message) = ErrorOf(response);
        Assert.Equal(-32602, code);
        Assert.Contains("DoesNotExist", message);
        // Pre-fix the call silently answered from the active project's graph.
        Assert.DoesNotContain("Widget", message);
    }

    [Fact]
    public async Task ToolCall_TenantLockedToDeletedProject_Errors_InsteadOfCrossTenantFallback()
    {
        var (pm, synth, _) = await SetupAsync();
        // Simulates the SaaS race: the authenticated tenant's project vanished from the registry
        // between auth and query. Pre-fix this resolved to the ACTIVE project (another tenant's graph).
        var handler = new McpRequestHandler(pm, synth, contextProjectName: "GhostTenant", lockToContextProject: true);

        var response = await handler.ProcessJsonRpcMessageAsync(
            ToolCall("references", new { symbol = "Widget" }));

        var (code, message) = ErrorOf(response);
        Assert.Equal(-32602, code);
        Assert.Contains("GhostTenant", message);
    }

    [Fact]
    public async Task SetProject_NonPersistentSession_RefusesInsteadOfClaimingSuccess()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, persistentSession: false);

        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("set_project", new { name = "P" })));

        Assert.Contains("not supported over the per-request HTTP relay", text);
        Assert.DoesNotContain("is now", text);
    }

    [Fact]
    public async Task SetProject_PersistentSession_StillSwitches()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth);

        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("set_project", new { name = "P" })));

        Assert.Contains("Active project for this session is now 'P'", text);
    }

    [Fact]
    public async Task SetProject_UnknownName_ListsAvailable_WithoutSwitching()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth);

        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("set_project", new { name = "Nope" })));

        Assert.Contains("No project named 'Nope'", text);
    }
}
