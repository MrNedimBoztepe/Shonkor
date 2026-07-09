// Licensed to Shonkor under the MIT License.

extern alias bench;

using bench::Shonkor.Bench;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// Unit tests for the answer-groundedness metric logic (TICKET-201): citation parsing/validation,
/// must-cite recall, abstention recall/precision, uncited-paragraph rate and content checks — driven
/// by a scripted analyzer so no LLM backend is needed.
/// </summary>
public class AnswersBenchmarkTests
{
    /// <summary>Returns a canned answer per question; records the context it was handed.</summary>
    private sealed class ScriptedAnalyzer(Func<string, string> answerFor) : ISemanticAnalyzer
    {
        public Task<SemanticAnalysisResult> AnalyzeNodeAsync(GraphNode node, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string> GenerateRAGResponseAsync(string query, IReadOnlyList<GraphNode> contextNodes, CancellationToken ct = default) =>
            Task.FromResult(answerFor(query));
    }

    private static async Task<SqliteGraphStorageProvider> CreateStorageAsync()
    {
        var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();
        await storage.UpsertNodesAsync(new[]
        {
            new GraphNode { Id = "/repo/A.cs::N.Widget", Name = "Widget", Type = "Class", FilePath = "/repo/A.cs", Content = "class Widget {}" },
            new GraphNode { Id = "/repo/B.cs::N.Gadget", Name = "Gadget", Type = "Class", FilePath = "/repo/B.cs", Content = "class Gadget {}" }
        });
        return storage;
    }

    [Fact]
    public async Task GroundedAnswer_WithValidCitations_ScoresPerfect()
    {
        using var storage = await CreateStorageAsync();
        var analyzer = new ScriptedAnalyzer(_ =>
            "Der Widget-Typ ist eine Klasse [Widget @ A.cs:1-1].\n\nSie hat keine Abhängigkeiten [Widget @ A.cs:1-1].");

        var result = await AnswersBenchmark.RunAsync(storage, analyzer, new[]
        {
            new AnswerCase { Id = "c1", Question = "Was ist Widget?", ContextNodeIds = ["Widget"], MustCite = ["Widget"] }
        }, TextWriter.Null);

        Assert.Equal(1.0, result.CitationValidity);
        Assert.Equal(1.0, result.MustCiteRecall);
        Assert.Equal(0.0, result.UncitedParagraphRate);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task InventedCitation_LowersValidity_AndIsReported()
    {
        using var storage = await CreateStorageAsync();
        var analyzer = new ScriptedAnalyzer(_ =>
            "Das regelt der PaymentService [PaymentService @ Pay.cs:1-10], gemeinsam mit [Widget @ A.cs:1-1].");

        var result = await AnswersBenchmark.RunAsync(storage, analyzer, new[]
        {
            new AnswerCase { Id = "c1", Question = "Was ist Widget?", ContextNodeIds = ["Widget"], MustCite = ["Widget"] }
        }, TextWriter.Null);

        Assert.Equal(0.5, result.CitationValidity); // 1 of 2 citations resolves to the context
        Assert.Contains(result.Failures, f => f.Contains("PaymentService"));
    }

    [Fact]
    public async Task MustCiteMiss_And_UncitedParagraphs_AreMeasured()
    {
        using var storage = await CreateStorageAsync();
        var analyzer = new ScriptedAnalyzer(_ =>
            "Erster Absatz ohne Beleg.\n\nZweiter Absatz zitiert [Gadget @ B.cs:1-1].");

        var result = await AnswersBenchmark.RunAsync(storage, analyzer, new[]
        {
            new AnswerCase { Id = "c1", Question = "Frage", ContextNodeIds = ["Widget", "Gadget"], MustCite = ["Widget"] }
        }, TextWriter.Null);

        Assert.Equal(0.0, result.MustCiteRecall);        // Gadget cited, Widget expected
        Assert.Equal(0.5, result.UncitedParagraphRate);  // 1 of 2 paragraphs uncited
        Assert.Equal(1.0, result.CitationValidity);      // the one citation IS in the context
    }

    [Fact]
    public async Task AbstentionRecallAndPrecision_CoverBothFailureDirections()
    {
        using var storage = await CreateStorageAsync();
        var analyzer = new ScriptedAnalyzer(question => question switch
        {
            "abstain-ok" => "Das ist in den aktuellen Graphen-Daten nicht belegt.",
            "abstain-miss" => "Natürlich, das System nutzt Redis mit LRU-Eviction [Widget @ A.cs:1-1].",
            "answerable-falseabstain" => "Das ist in den aktuellen Graphen-Daten nicht belegt.",
            _ => "Antwort [Widget @ A.cs:1-1]."
        });

        var result = await AnswersBenchmark.RunAsync(storage, analyzer, new[]
        {
            new AnswerCase { Id = "a1", Question = "abstain-ok", ContextNodeIds = ["Widget"], Kind = "abstain" },
            new AnswerCase { Id = "a2", Question = "abstain-miss", ContextNodeIds = ["Widget"], Kind = "abstain" },
            new AnswerCase { Id = "a3", Question = "answerable-falseabstain", ContextNodeIds = ["Widget"] },
            new AnswerCase { Id = "a4", Question = "answerable-ok", ContextNodeIds = ["Widget"] }
        }, TextWriter.Null);

        Assert.Equal(0.5, result.AbstentionRecall);    // 1 of 2 abstain cases abstained
        Assert.Equal(0.5, result.AbstentionPrecision); // 1 correct abstention vs 1 false abstention
        Assert.Contains(result.Failures, f => f.StartsWith("a2:"));
        Assert.Contains(result.Failures, f => f.StartsWith("a3:"));
    }

    [Fact]
    public async Task ContentChecks_AndUnresolvableContext_AreReported()
    {
        using var storage = await CreateStorageAsync();
        var analyzer = new ScriptedAnalyzer(_ => "Die Antwort erwähnt SQLite [Widget @ A.cs:1-1].");

        var result = await AnswersBenchmark.RunAsync(storage, analyzer, new[]
        {
            new AnswerCase { Id = "c1", Question = "Frage", ContextNodeIds = ["Widget"], MustContain = ["SQLite"], MustNotContain = ["Postgres"] },
            new AnswerCase { Id = "c2", Question = "Frage", ContextNodeIds = ["DoesNotExist"] }
        }, TextWriter.Null);

        Assert.Equal(1.0, result.ContentCheckPassRate);
        Assert.Equal(1, result.Skipped);
        Assert.Contains(result.Failures, f => f.StartsWith("c2:") && f.Contains("SKIPPED"));
    }

    [Fact]
    public async Task ContextResolution_AcceptsExactNodeIds_AndSymbolNames()
    {
        using var storage = await CreateStorageAsync();
        List<GraphNode>? seenContext = null;
        var analyzer = new ScriptedAnalyzer(_ => "ok [Widget @ A.cs:1-1]");
        // Capture via a wrapper case: both the raw id and the bare name must resolve to nodes.
        var capture = new CapturingAnalyzer(ctx => seenContext = ctx);

        await AnswersBenchmark.RunAsync(storage, capture, new[]
        {
            new AnswerCase { Id = "c1", Question = "Frage", ContextNodeIds = ["/repo/A.cs::N.Widget", "Gadget"] }
        }, TextWriter.Null);

        Assert.NotNull(seenContext);
        Assert.Equal(2, seenContext!.Count);
        Assert.Contains(seenContext, n => n.Name == "Widget");
        Assert.Contains(seenContext, n => n.Name == "Gadget");
    }

    private sealed class CapturingAnalyzer(Action<List<GraphNode>> onContext) : ISemanticAnalyzer
    {
        public Task<SemanticAnalysisResult> AnalyzeNodeAsync(GraphNode node, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string> GenerateRAGResponseAsync(string query, IReadOnlyList<GraphNode> contextNodes, CancellationToken ct = default)
        {
            onContext(contextNodes.ToList());
            return Task.FromResult("ok [Widget @ A.cs:1-1]");
        }
    }
}
