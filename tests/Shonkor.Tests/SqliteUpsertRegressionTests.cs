// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// Regression tests for BUG-001 (NaN poisoning the semantic top-K heap) and BUG-002/BUG-005
/// (INSERT OR REPLACE corrupting the FTS index and wiping enrichment on re-upsert).
/// </summary>
public class SqliteUpsertRegressionTests
{
    private static GraphNode MakeNode(string id, string content, string? hash = null) => new()
    {
        Id = id,
        Type = "Class",
        Name = id,
        Content = content,
        FilePath = $"C:\\repo\\{id}.cs",
        ContentHash = hash
    };

    private static async Task<SqliteGraphStorageProvider> CreateStorageAsync()
    {
        var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();
        return storage;
    }

    private static float[] UnitVector(int dim, int hotIndex)
    {
        var v = new float[dim];
        v[hotIndex] = 1f;
        return v;
    }

    // BUG-001: a zero-magnitude vector yields a NaN cosine score. NaN must neither block real
    // results out of the heap nor hang the request (Math.BitIncrement(NaN) == NaN loops forever).
    [Fact]
    public async Task SearchSemantic_ZeroMagnitudeVectors_AreIgnoredAndSearchTerminates()
    {
        using var storage = await CreateStorageAsync();

        var zeroA = MakeNode("ZeroA", "zero vector a");
        zeroA.Embedding = new float[8];
        var zeroB = MakeNode("ZeroB", "zero vector b");
        zeroB.Embedding = new float[8];
        var real = MakeNode("Real", "real vector");
        real.Embedding = UnitVector(8, 0);

        await storage.UpsertNodesAsync(new[] { zeroA, zeroB, real });

        // Pre-fix this call never returned (two NaN keys -> BitIncrement loop); guard with a timeout
        // so a regression fails the test instead of hanging the run.
        var search = storage.SearchSemanticAsync(UnitVector(8, 0), maxResults: 3);
        var results = await search.WaitAsync(TimeSpan.FromSeconds(15));

        var hit = Assert.Single(results, r => r.Node.Id == "Real");
        Assert.Equal(1.0, hit.Score, precision: 3);
        Assert.DoesNotContain(results, r => r.Node.Id is "ZeroA" or "ZeroB");
    }

    // BUG-002: INSERT OR REPLACE deleted the old row without firing the FTS delete trigger,
    // leaving a ghost FTS entry per re-upsert. Old content must stop matching after an update.
    [Fact]
    public async Task Upsert_SameId_UpdatesFtsInsteadOfLeavingGhostEntries()
    {
        using var storage = await CreateStorageAsync();

        await storage.UpsertNodesAsync(new[] { MakeNode("Doc", "alphacontent shared", hash: "h1") });
        await storage.UpsertNodesAsync(new[] { MakeNode("Doc", "betacontent shared", hash: "h2") });
        await storage.UpsertNodesAsync(new[] { MakeNode("Doc", "betacontent shared", hash: "h2") });

        var oldHits = await storage.SearchAsync("alphacontent");
        Assert.Empty(oldHits);

        var newHits = await storage.SearchAsync("betacontent");
        Assert.Equal("Doc", Assert.Single(newHits).Node.Id);

        // A term present in both versions must match exactly once, not once per ghost entry.
        var sharedHits = await storage.SearchAsync("shared");
        Assert.Equal("Doc", Assert.Single(sharedHits).Node.Id);
    }

    // BUG-005: re-upserting an unchanged node must not wipe Summary/Embedding/EmbeddingDim/
    // EmbeddingModel and must not re-queue the node for semantic analysis.
    [Fact]
    public async Task Upsert_UnchangedContent_PreservesEnrichment()
    {
        using var storage = await CreateStorageAsync();
        var node = MakeNode("Svc", "public class Svc { }", hash: "h1");

        await storage.UpsertNodesAsync(new[] { node });
        await storage.UpdateNodeSemanticDataAsync("Svc",
            new SemanticAnalysisResult { Summary = "Business service." },
            embedding: UnitVector(8, 1), embeddingModel: "test-model");
        Assert.Empty(await storage.GetNodesPendingSemanticAnalysisAsync(10));

        // Re-upsert the identical node (as an incremental re-index would).
        await storage.UpsertNodesAsync(new[] { MakeNode("Svc", "public class Svc { }", hash: "h1") });

        var reloaded = await storage.GetNodeByIdAsync("Svc");
        Assert.NotNull(reloaded);
        Assert.Equal("Business service.", reloaded!.Summary);
        Assert.Empty(await storage.GetNodesPendingSemanticAnalysisAsync(10));

        // Embedding still searchable.
        var results = await storage.SearchSemanticAsync(UnitVector(8, 1), maxResults: 1);
        Assert.Equal("Svc", Assert.Single(results).Node.Id);

        // Dim/model stamps survived: nothing is stale for the matching pair...
        Assert.Equal(0, await storage.MarkStaleEmbeddingsForReembedAsync(8, "test-model"));
        // ...and a dimension change is detected (proves EmbeddingDim is still populated).
        Assert.Equal(1, await storage.MarkStaleEmbeddingsForReembedAsync(16, "test-model"));
    }

    [Fact]
    public async Task Upsert_ChangedContent_InvalidatesEnrichmentAndRequeues()
    {
        using var storage = await CreateStorageAsync();

        await storage.UpsertNodesAsync(new[] { MakeNode("Svc", "public class Svc { }", hash: "h1") });
        await storage.UpdateNodeSemanticDataAsync("Svc",
            new SemanticAnalysisResult { Summary = "Business service." },
            embedding: UnitVector(8, 1), embeddingModel: "test-model");

        await storage.UpsertNodesAsync(new[] { MakeNode("Svc", "public class Svc { int x; }", hash: "h2") });

        var reloaded = await storage.GetNodeByIdAsync("Svc");
        Assert.NotNull(reloaded);
        Assert.Null(reloaded!.Summary);

        var pending = await storage.GetNodesPendingSemanticAnalysisAsync(10);
        Assert.Contains(pending, n => n.Id == "Svc");

        // Stale embedding is gone from semantic search.
        Assert.Empty(await storage.SearchSemanticAsync(UnitVector(8, 1), maxResults: 5));
    }

    [Fact]
    public async Task Upsert_WithCallerProvidedEmbedding_StampsDimension()
    {
        using var storage = await CreateStorageAsync();
        var node = MakeNode("Vec", "content", hash: "h1");
        node.Embedding = UnitVector(8, 2);

        await storage.UpsertNodesAsync(new[] { node });

        // A differing expected dimension marks the row stale — proving EmbeddingDim was stored.
        Assert.Equal(1, await storage.MarkStaleEmbeddingsForReembedAsync(16));
    }
}
