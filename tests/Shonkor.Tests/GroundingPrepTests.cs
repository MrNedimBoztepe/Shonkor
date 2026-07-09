// Licensed to Shonkor under the MIT License.

using Microsoft.Extensions.Configuration;
using Shonkor.Core.Models;
using Shonkor.Infrastructure.Storage;
using Shonkor.Web;
using Shonkor.Web.Endpoints;

namespace Shonkor.Tests;

/// <summary>
/// Tests the server-side grounding preparation (TICKET-206): the relevance-floor abstain-without-LLM
/// short-circuit, per-node match strength, history mapping, and injection-flag annotation of context nodes.
/// </summary>
public class GroundingPrepTests
{
    private static async Task<SqliteGraphStorageProvider> StorageAsync(params GraphNode[] nodes)
    {
        var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();
        if (nodes.Length > 0) await storage.UpsertNodesAsync(nodes);
        return storage;
    }

    private static GraphNode Node(string id, string name) =>
        new() { Id = id, Name = name, Type = "Class", FilePath = $"{name}.cs", StartLine = 1, EndLine = 3, Content = $"class {name} {{}}" };

    private static IConfiguration Config(params (string, string)[] kv) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            kv.Select(p => new KeyValuePair<string, string?>(p.Item1, p.Item2))).Build();

    [Fact]
    public async Task BelowFloor_AllScores_AbstainsWithoutLlm()
    {
        using var storage = await StorageAsync(Node("id-a", "Alpha"), Node("id-b", "Beta"));
        var req = new AskRagRequest("Frage", ["id-a", "id-b"], Scores: [0.1, 0.15]);
        var config = Config(("Rag:MinRelevanceScore", "0.4"));

        var prep = await GroundingPrep.BuildAsync(req, storage, config, default);

        Assert.True(prep.NoEvidence); // deterministic abstention, no context handed to a model
        Assert.Empty(prep.ContextNodes);
        Assert.Contains("nicht belegt", prep.AbstentionText);
    }

    [Fact]
    public async Task PartiallyAboveFloor_KeepsOnlyStrongContext_AndSetsMatchStrength()
    {
        using var storage = await StorageAsync(Node("id-a", "Alpha"), Node("id-b", "Beta"));
        var req = new AskRagRequest("Frage", ["id-a", "id-b"], Scores: [0.9, 0.1]);
        var config = Config(("Rag:MinRelevanceScore", "0.4"));

        var prep = await GroundingPrep.BuildAsync(req, storage, config, default);

        Assert.False(prep.NoEvidence);
        Assert.Single(prep.ContextNodes);
        Assert.Equal("id-a", prep.ContextNodes[0].Id);
        Assert.NotNull(prep.Options.MatchStrength);
        Assert.Equal(0.9, prep.Options.MatchStrength!["id-a"]);
    }

    [Fact]
    public async Task NoFloorConfigured_KeepsAllResolvedNodes_EvenWithLowScores()
    {
        using var storage = await StorageAsync(Node("id-a", "Alpha"), Node("id-b", "Beta"));
        var req = new AskRagRequest("Frage", ["id-a", "id-b"], Scores: [0.01, 0.02]);

        var prep = await GroundingPrep.BuildAsync(req, storage, Config(), default);

        Assert.False(prep.NoEvidence); // floor defaults to 0 = off
        Assert.Equal(2, prep.ContextNodes.Count);
    }

    [Fact]
    public async Task NoScores_NeverAbstainsOnThreshold_EvenWithFloorSet()
    {
        using var storage = await StorageAsync(Node("id-a", "Alpha"));
        var req = new AskRagRequest("Frage", ["id-a"]); // no scores provided
        var config = Config(("Rag:MinRelevanceScore", "0.4"));

        var prep = await GroundingPrep.BuildAsync(req, storage, config, default);

        Assert.False(prep.NoEvidence);
        Assert.Single(prep.ContextNodes);
    }

    [Fact]
    public async Task FlaggedContextNode_IsAnnotated_FromDiagnostics()
    {
        using var storage = await StorageAsync(Node("id-a", "Alpha"), Node("id-b", "Beta"));
        await storage.ReplaceDiagnosticsAsync("security.suspicious-content", new[]
        {
            new GraphDiagnostic(
                Code: "security.suspicious-instruction-in-content",
                Severity: DiagnosticSeverity.Warning,
                Message: "looks like an injected instruction",
                NodeId: "id-b")
        });
        var req = new AskRagRequest("Frage", ["id-a", "id-b"]);

        var prep = await GroundingPrep.BuildAsync(req, storage, Config(), default);

        Assert.Contains("id-b", prep.Options.FlaggedNodeIds);
        Assert.DoesNotContain("id-a", prep.Options.FlaggedNodeIds);
    }

    [Fact]
    public async Task History_IsMappedAndTrimmed_OfBlankTurns()
    {
        using var storage = await StorageAsync(Node("id-a", "Alpha"));
        var req = new AskRagRequest("Frage", ["id-a"],
            History: [new ChatTurnDto("user", "erste Frage"), new ChatTurnDto("assistant", "   ")]);

        var prep = await GroundingPrep.BuildAsync(req, storage, Config(), default);

        Assert.Single(prep.Options.History); // the blank assistant turn is dropped
        Assert.Equal("erste Frage", prep.Options.History[0].Text);
    }
}
