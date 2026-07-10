// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Models;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// TICKET-211: Concept nodes carry no body, so they were never embedded and stayed invisible to semantic
/// search. They now embed from name + connected node names, selected by a self-terminating "no vector yet"
/// predicate (concepts stay excluded from LLM analysis — there is nothing to summarize).
/// </summary>
public class ConceptEmbeddingTests
{
    private static string NewDbPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"shonkor_concept_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "g.db");
    }

    [Fact]
    public void BuildConcept_FoldsSummaryAndConnectedNames_IntoTheDocument()
    {
        var doc = EmbeddingTextBuilder.BuildConcept(
            "idempotency", "Operations safe to repeat.", new[] { "PaymentProcessor", "RetryPolicy" });

        Assert.Contains("Concept idempotency", doc);
        Assert.Contains("Operations safe to repeat.", doc);
        Assert.Contains("PaymentProcessor", doc);
        Assert.Contains("RetryPolicy", doc);
    }

    [Fact]
    public void BuildConcept_WithNoNeighboursOrSummary_IsJustTheName()
    {
        var doc = EmbeddingTextBuilder.BuildConcept("caching", null, Array.Empty<string>());
        Assert.Equal("Concept caching", doc);
    }

    [Fact]
    public async Task PendingConcepts_AreThoseWithoutAnEmbedding_AndCarryTheirNeighbourNames()
    {
        var dbPath = NewDbPath();
        using var storage = new SqliteGraphStorageProvider(dbPath);
        await storage.InitializeAsync();

        await storage.UpsertNodesAsync(new[]
        {
            new GraphNode { Id = "concept::idempotency", Name = "idempotency", Type = "Concept" },
            new GraphNode { Id = "cls::PaymentProcessor", Name = "PaymentProcessor", Type = "Class", Content = "class PaymentProcessor { }" },
            new GraphNode { Id = "cls::RetryPolicy", Name = "RetryPolicy", Type = "Class", Content = "class RetryPolicy { }" }
        });
        await storage.UpsertEdgesAsync(new[]
        {
            new GraphEdge { SourceId = "cls::PaymentProcessor", TargetId = "concept::idempotency", Relationship = "RELATES_TO" },
            new GraphEdge { SourceId = "concept::idempotency", TargetId = "cls::RetryPolicy", Relationship = "RELATES_TO" }
        });

        var pending = await storage.GetConceptsPendingEmbeddingAsync(10);

        var concept = Assert.Single(pending);
        Assert.Equal("idempotency", concept.Name);
        // Neighbours are collected in BOTH directions.
        Assert.Contains("PaymentProcessor", concept.ConnectedNames);
        Assert.Contains("RetryPolicy", concept.ConnectedNames);
    }

    [Fact]
    public async Task OnceEmbedded_AConceptIsNoLongerPending_SoThePassTerminates()
    {
        var dbPath = NewDbPath();
        using var storage = new SqliteGraphStorageProvider(dbPath);
        await storage.InitializeAsync();
        await storage.UpsertNodesAsync(new[]
        {
            new GraphNode { Id = "concept::caching", Name = "caching", Type = "Concept" }
        });

        Assert.Single(await storage.GetConceptsPendingEmbeddingAsync(10));

        await storage.UpdateNodeEmbeddingAsync("concept::caching", new[] { 0.1f, 0.2f, 0.3f }, "test-model");

        // "no vector yet" is the pending predicate — no flag needed, and no infinite re-selection.
        Assert.Empty(await storage.GetConceptsPendingEmbeddingAsync(10));
    }

    [Fact]
    public async Task NonConceptNodesWithoutEmbeddings_AreNotReturned()
    {
        var dbPath = NewDbPath();
        using var storage = new SqliteGraphStorageProvider(dbPath);
        await storage.InitializeAsync();
        await storage.UpsertNodesAsync(new[]
        {
            new GraphNode { Id = "cls::Plain", Name = "Plain", Type = "Class", Content = "class Plain { }" }
        });

        Assert.Empty(await storage.GetConceptsPendingEmbeddingAsync(10));
    }

    [Fact]
    public async Task ConceptWithNoEdges_IsStillPending_AndHasNoNeighbours()
    {
        var dbPath = NewDbPath();
        using var storage = new SqliteGraphStorageProvider(dbPath);
        await storage.InitializeAsync();
        await storage.UpsertNodesAsync(new[]
        {
            new GraphNode { Id = "concept::orphan", Name = "orphan", Type = "Concept" }
        });

        var concept = Assert.Single(await storage.GetConceptsPendingEmbeddingAsync(10));
        Assert.Empty(concept.ConnectedNames);
        Assert.Equal("Concept orphan", EmbeddingTextBuilder.BuildConcept(concept.Name, concept.Summary, concept.ConnectedNames));
    }
}
