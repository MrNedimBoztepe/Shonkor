// Licensed to Shonkor under the MIT License.

using System.Text.Json;
using Shonkor.Core.Models;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// TICKET-214: the MCP generate_capsule tool must use the budget-aware synthesizer (seeds first and in
/// full, hub-capped, omission notices) like the web /api/capsule path — not the old FTS-only seed +
/// legacy full-render + blind "truncate at the last ## before maxChars", which could drop the very seed
/// that matched the query.
/// </summary>
public class GenerateCapsuleBudgetTests
{
    private static async Task<McpRequestHandler> SetupAsync(Func<SqliteGraphStorageProvider, Task> seed)
    {
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_capsule_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        var dbPath = Path.Combine(ws, "g.db");
        using (var storage = new SqliteGraphStorageProvider(dbPath))
        {
            await storage.InitializeAsync();
            await seed(storage);
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

    private static string Call(object args) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "generate_capsule", arguments = args } });

    private static string ResultText(string? response)
    {
        using var doc = JsonDocument.Parse(response!);
        return doc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
    }

    [Fact]
    public async Task SeedBody_SurvivesInFull_EvenWhenAlphabeticallyLast_UnderASmallBudget()
    {
        // The seed's file path sorts AFTER the filler nodes' — the legacy file-grouped renderer emitted it
        // last, so a small maxChars truncated exactly it. A unique token ties the query to that one node.
        var seedBody = "public sealed class Zebra { /* UNIQUENEEDLE marker */ " + new string('x', 3000) + " }";
        var handler = await SetupAsync(async storage =>
        {
            var nodes = new List<GraphNode>
            {
                new() { Id = "z::seed", Name = "UNIQUENEEDLE", Type = "Class", FilePath = "zzz_last.cs", StartLine = 1, Content = seedBody }
            };
            // A dozen higher-degree filler nodes sorting BEFORE the seed and big enough to blow the budget.
            for (var i = 0; i < 12; i++)
            {
                nodes.Add(new GraphNode
                {
                    Id = $"a::filler{i}",
                    Name = $"Filler{i}",
                    Type = "Class",
                    FilePath = $"aaa_{i:D2}.cs",
                    StartLine = 1,
                    Content = "public class Filler" + i + " { " + new string('y', 2000) + " }"
                });
            }
            await storage.UpsertNodesAsync(nodes);
            // Give the filler nodes edges (higher degree) so degree-ranking would prefer them over the seed.
            var edges = new List<GraphEdge>();
            for (var i = 0; i < 12; i++)
                for (var j = 0; j < 12; j++)
                    if (i != j) edges.Add(new GraphEdge { SourceId = $"a::filler{i}", TargetId = $"a::filler{j}", Relationship = "REFERENCES_TYPE" });
            await storage.UpsertEdgesAsync(edges);
        });

        var capsule = ResultText(await handler.ProcessJsonRpcMessageAsync(Call(new { query = "UNIQUENEEDLE", hops = 1, maxChars = 1500 })));

        // The seed body is present IN FULL despite a 1500-char budget and 12 higher-degree filler nodes.
        Assert.Contains("UNIQUENEEDLE marker", capsule);
        Assert.Contains("class Zebra", capsule);
    }

    [Fact]
    public async Task LowerRelevanceBodies_AreOmittedWithANotice_NotSilentlyTruncated()
    {
        var handler = await SetupAsync(async storage =>
        {
            var nodes = new List<GraphNode>
            {
                new() { Id = "seed::1", Name = "NEEDLE", Type = "Class", FilePath = "seed.cs", StartLine = 1, Content = "class Needle { " + new string('n', 400) + " }" }
            };
            for (var i = 0; i < 8; i++)
                nodes.Add(new GraphNode { Id = $"nb::{i}", Name = $"Neighbour{i}", Type = "Class", FilePath = $"nb{i}.cs", StartLine = 1, Content = "class Neighbour" + i + " { " + new string('z', 3000) + " }" });
            await storage.UpsertNodesAsync(nodes);
            var edges = Enumerable.Range(0, 8).Select(i => new GraphEdge { SourceId = "seed::1", TargetId = $"nb::{i}", Relationship = "REFERENCES_TYPE" }).ToList();
            await storage.UpsertEdgesAsync(edges);
        });

        var capsule = ResultText(await handler.ProcessJsonRpcMessageAsync(Call(new { query = "NEEDLE", hops = 1, maxChars = 1000 })));

        // The budget synthesizer states what it omitted; it never cuts the markdown mid-structure.
        Assert.Contains("omitted", capsule, StringComparison.OrdinalIgnoreCase);
        // The old truncation notice must be gone.
        Assert.DoesNotContain("Capsule truncated to fit the requested character budget", capsule);
    }

    [Fact]
    public async Task NoMatch_ReturnsAPlainMessage()
    {
        var handler = await SetupAsync(async storage =>
            await storage.UpsertNodesAsync(new[] { new GraphNode { Id = "x::1", Name = "Something", Type = "Class", Content = "class Something {}" } }));

        var text = ResultText(await handler.ProcessJsonRpcMessageAsync(Call(new { query = "zzzznomatchzzzz" })));
        Assert.Contains("No nodes found", text);
    }
}
