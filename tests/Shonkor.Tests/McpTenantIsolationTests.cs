// Licensed to Shonkor under the MIT License.

using System.Text.Json;

using Shonkor.Core.Models;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// Regression tests for MCP multi-tenant isolation. An authenticated SaaS caller must not be able
/// to reach another tenant's graph by passing a different <c>projectName</c> in a tool call.
/// </summary>
public class McpTenantIsolationTests
{
    private const string SharedSearchTerm = "tenantmarker";

    [Fact]
    public async Task TenantLocked_IgnoresProjectNameArgument_PreventingCrossTenantAccess()
    {
        var (pm, synth, _) = await SetupTwoTenantsAsync();

        // Caller is authenticated for ProjA and the session is tenant-locked (the SaaS/relay case).
        var handler = new McpRequestHandler(pm, synth, contextProjectName: "ProjA", lockToContextProject: true);

        // The caller tries to redirect the lookup to ProjB via the tool argument.
        var response = await handler.ProcessJsonRpcMessageAsync(LocateRequest("ProjB"));

        Assert.NotNull(response);
        // Must see ProjA's node, never ProjB's — the projectName argument must be ignored when locked.
        Assert.Contains("AlphaNode", response);
        Assert.DoesNotContain("BetaNode", response);
    }

    [Fact]
    public async Task NotLocked_HonorsProjectNameArgument_ForLocalProjectSwitching()
    {
        var (pm, synth, _) = await SetupTwoTenantsAsync();

        // Local/stdio (CLI) or trusted-dev case: free project switching is intentional.
        var handler = new McpRequestHandler(pm, synth, contextProjectName: "ProjA", lockToContextProject: false);

        var response = await handler.ProcessJsonRpcMessageAsync(LocateRequest("ProjB"));

        Assert.NotNull(response);
        // Here the projectName argument SHOULD switch to ProjB.
        Assert.Contains("BetaNode", response);
        Assert.DoesNotContain("AlphaNode", response);
    }

    private static string LocateRequest(string projectName) =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new
            {
                name = "locate",
                arguments = new { query = SharedSearchTerm, projectName }
            }
        });

    /// <summary>
    /// Creates a temp workspace with two registered projects (ProjA, ProjB), each backed by its own
    /// SQLite database seeded with a single distinguishable node that shares a common search term.
    /// </summary>
    private static async Task<(ProjectManager Pm, ContextCapsuleSynthesizer Synth, string Workspace)> SetupTwoTenantsAsync()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"shonkor_tenant_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);

        var dbA = Path.Combine(workspace, "a.db");
        var dbB = Path.Combine(workspace, "b.db");

        await SeedNodeAsync(dbA, id: "node-a", name: "AlphaNode");
        await SeedNodeAsync(dbB, id: "node-b", name: "BetaNode");

        var registry = new
        {
            Organizations = Array.Empty<object>(),
            Users = Array.Empty<object>(),
            Projects = new[]
            {
                new { Name = "ProjA", Path = workspace, DatabasePath = dbA, OrganizationId = "", RepositoryUrl = "", ApiKey = "" },
                new { Name = "ProjB", Path = workspace, DatabasePath = dbB, OrganizationId = "", RepositoryUrl = "", ApiKey = "" }
            },
            ActiveProjectName = "ProjA"
        };
        File.WriteAllText(Path.Combine(workspace, "projects.json"), JsonSerializer.Serialize(registry));

        var pm = new ProjectManager(workspace);
        return (pm, new ContextCapsuleSynthesizer(), workspace);
    }

    private static async Task SeedNodeAsync(string dbPath, string id, string name)
    {
        using var storage = new SqliteGraphStorageProvider(dbPath);
        await storage.InitializeAsync();
        await storage.UpsertNodesAsync(new[]
        {
            new GraphNode
            {
                Id = id,
                Name = name,
                Type = "Code",
                Content = SharedSearchTerm,
                FilePath = $"{name}.cs"
            }
        });
    }
}
