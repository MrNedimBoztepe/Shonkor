// Licensed to Shonkor under the MIT License.

using System.Text.Json;
using Shonkor.Core.Models;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// Tests the provenance-weighted reverse blast radius tool: reverse (dependents) traversal, shortest depth,
/// weakest-tier-along-best-path, minTier pruning, maxDepth truncation, structural-edge skip, and cycle safety.
/// </summary>
public class BlastRadiusTests
{
    // Edge direction: Source = dependent, Target = dependency. "Who depends on A" = incoming edges to A.
    private static Provenance E => Provenance.Extracted;
    private static Provenance Amb => Provenance.Ambiguous;

    private static async Task<McpRequestHandler> SetupAsync()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_blast_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        var dbPath = Path.Combine(ws, "g.db");

        GraphNode N(string id) => new() { Id = id, Name = id, Type = "Class", FilePath = Path.Combine(ws, id + ".cs"), StartLine = 1, Content = $"class {id}" };
        GraphEdge Ref(string dependent, string dependency, Provenance p) =>
            new() { SourceId = dependent, TargetId = dependency, Relationship = "REFERENCES_TYPE", Provenance = p };

        using (var storage = new SqliteGraphStorageProvider(dbPath))
        {
            await storage.InitializeAsync();
            await storage.UpsertNodesAsync(new[] { "A", "B", "C", "G", "M", "H", "P", "Q", "cyc1", "cyc2" }
                .Select(N).Append(new GraphNode { Id = Path.Combine(ws, "A.cs"), Name = "A.cs", Type = "File", FilePath = Path.Combine(ws, "A.cs") }));

            await storage.UpsertEdgesAsync(new[]
            {
                Ref("B", "A", E),       // B depends directly on A (depth 1, extracted)
                Ref("C", "B", E),       // C -> B -> A (A reached at depth 2)
                Ref("G", "A", Amb),     // G depends on A via an AMBIGUOUS edge only
                Ref("M", "A", Amb),     // M -> A ambiguous
                Ref("H", "M", E),       // H -> M(extracted) -> A(ambiguous): weakest link = ambiguous
                Ref("Q", "A", E),       // Q -> A extracted
                Ref("P", "Q", E),       // P -> Q -> A (extracted, depth 2)
                Ref("P", "A", Amb),     // P -> A ambiguous (depth 1): P has a shorter ambiguous + longer extracted path
                Ref("cyc2", "A", E),    // cyc2 -> A extracted
                Ref("cyc1", "cyc2", E), // cyc1 -> cyc2
                Ref("cyc2", "cyc1", E), // cyc2 -> cyc1  (cycle between cyc1/cyc2)
                // Structural containment must be IGNORED by impact traversal.
                new GraphEdge { SourceId = Path.Combine(ws, "A.cs"), TargetId = "A", Relationship = "CONTAINS", Provenance = E }
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
        return new McpRequestHandler(new ProjectManager(ws), new ContextCapsuleSynthesizer(), "P", lockToContextProject: true);
    }

    private static string ToolCall(object args) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "blast_radius", arguments = args } });

    private static JsonElement ResultOf(string? response)
    {
        using var doc = JsonDocument.Parse(response!);
        var text = doc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>affected node name → (depth, minTierAlongPath).</summary>
    private static Dictionary<string, (int Depth, string Tier)> Affected(JsonElement result)
    {
        var map = new Dictionary<string, (int, string)>();
        foreach (var a in result.GetProperty("affected").EnumerateArray())
            map[a.GetProperty("name").GetString()!] = (a.GetProperty("depth").GetInt32(), a.GetProperty("minTierAlongPath").GetString()!);
        return map;
    }

    [Fact]
    public async Task DirectDependent_Depth1_ExtractedTier()
    {
        var handler = await SetupAsync();
        var r = ResultOf(await handler.ProcessJsonRpcMessageAsync(ToolCall(new { nodeOrFile = "A" })));
        var aff = Affected(r);

        Assert.Equal((1, "extracted"), aff["B"]);
    }

    [Fact]
    public async Task TransitivePath_TracksShortestDepth()
    {
        var handler = await SetupAsync();
        var aff = Affected(ResultOf(await handler.ProcessJsonRpcMessageAsync(ToolCall(new { nodeOrFile = "A" }))));

        Assert.Equal(1, aff["B"].Depth);
        Assert.Equal(2, aff["C"].Depth); // C -> B -> A
    }

    [Fact]
    public async Task AmbiguousOnlyPath_IsMarkedAmbiguous()
    {
        var handler = await SetupAsync();
        var aff = Affected(ResultOf(await handler.ProcessJsonRpcMessageAsync(ToolCall(new { nodeOrFile = "A" }))));

        Assert.Equal((1, "ambiguous"), aff["G"]);
    }

    [Fact]
    public async Task MixedPath_WeakestLinkWins()
    {
        var handler = await SetupAsync();
        var aff = Affected(ResultOf(await handler.ProcessJsonRpcMessageAsync(ToolCall(new { nodeOrFile = "A" }))));

        // H -> M(extracted) -> A(ambiguous): the path is only as strong as its weakest edge.
        Assert.Equal("ambiguous", aff["H"].Tier);
        Assert.Equal(2, aff["H"].Depth);
    }

    [Fact]
    public async Task MultiplePaths_BestGuaranteedTierWins_ShortestDepthKept()
    {
        var handler = await SetupAsync();
        var aff = Affected(ResultOf(await handler.ProcessJsonRpcMessageAsync(ToolCall(new { nodeOrFile = "A" }))));

        // P reaches A via a direct AMBIGUOUS edge (depth 1) and via P->Q->A all EXTRACTED (depth 2).
        // Best guaranteed tier = extracted; shortest depth = 1.
        Assert.Equal("extracted", aff["P"].Tier);
        Assert.Equal(1, aff["P"].Depth);
    }

    [Fact]
    public async Task MinTierExtracted_PrunesAmbiguousPaths_ResultIsSubset()
    {
        var handler = await SetupAsync();
        var full = Affected(ResultOf(await handler.ProcessJsonRpcMessageAsync(ToolCall(new { nodeOrFile = "A" }))));
        var strict = Affected(ResultOf(await handler.ProcessJsonRpcMessageAsync(ToolCall(new { nodeOrFile = "A", minTier = "extracted" }))));

        Assert.True(strict.Keys.All(full.ContainsKey)); // subset
        Assert.DoesNotContain("G", strict.Keys);         // ambiguous-only dependent is pruned
        Assert.DoesNotContain("H", strict.Keys);         // reachable only through an ambiguous edge
        Assert.Contains("B", strict.Keys);               // proven dependents remain
        Assert.All(strict.Values, v => Assert.Equal("extracted", v.Tier));
        // P survives via its extracted 2-hop path — but now at depth 2 (the ambiguous shortcut is pruned).
        Assert.Equal((2, "extracted"), strict["P"]);
    }

    [Fact]
    public async Task MaxDepth_TruncatesAndFlags()
    {
        var handler = await SetupAsync();
        var r = ResultOf(await handler.ProcessJsonRpcMessageAsync(ToolCall(new { nodeOrFile = "A", maxDepth = 1 })));
        var aff = Affected(r);

        Assert.True(r.GetProperty("truncatedAtDepth").GetBoolean());
        Assert.Contains("B", aff.Keys);      // depth 1 kept
        Assert.DoesNotContain("C", aff.Keys); // depth 2 pruned by the limit
    }

    [Fact]
    public async Task StructuralContainment_IsSkipped()
    {
        var handler = await SetupAsync();
        var aff = Affected(ResultOf(await handler.ProcessJsonRpcMessageAsync(ToolCall(new { nodeOrFile = "A" }))));

        Assert.DoesNotContain("A.cs", aff.Keys); // the containing File is not a dependent
    }

    [Fact]
    public async Task Cycle_Terminates_WithoutDoubleCounting()
    {
        var handler = await SetupAsync();
        var aff = Affected(ResultOf(await handler.ProcessJsonRpcMessageAsync(ToolCall(new { nodeOrFile = "A" }))));

        // cyc2 -> A (depth 1); cyc1 -> cyc2 -> A (depth 2); the cyc1<->cyc2 cycle must not loop.
        Assert.Equal(1, aff["cyc2"].Depth);
        Assert.Equal(2, aff["cyc1"].Depth);
    }

    [Fact]
    public async Task NoDerivedScores_InOutput()
    {
        var handler = await SetupAsync();
        using var doc = JsonDocument.Parse(await handler.ProcessJsonRpcMessageAsync(ToolCall(new { nodeOrFile = "A" }))!);
        var text = doc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

        foreach (var banned in new[] { "impactScore", "\"score\"", "centrality", "grade", "\"level\"" })
            Assert.DoesNotContain(banned, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NodeNotFound_CleanEmptyResult_NoException()
    {
        var handler = await SetupAsync();
        var r = ResultOf(await handler.ProcessJsonRpcMessageAsync(ToolCall(new { nodeOrFile = "DoesNotExist" })));

        Assert.False(r.GetProperty("found").GetBoolean());
        Assert.Empty(r.GetProperty("affected").EnumerateArray());
    }
}
