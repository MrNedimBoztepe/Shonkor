// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// #135: Concept nodes carry no FilePath, so the path-based reindex cleanup never removes them. When the
/// code that referenced a concept is deleted/re-indexed, the concept is orphaned and — being embedded —
/// pollutes semantic search. PruneOrphanConceptsAsync removes orphans; a normalized concept id dedups
/// near-duplicate LLM phrasings.
/// </summary>
public class ConceptHygieneTests
{
    private static string NewDbPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"shonkor_concept_hyg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "g.db");
    }

    private static GraphNode Code(string id) => new() { Id = id, Name = id, Type = "Class", Content = $"class {id} {{}}", FilePath = id + ".cs" };

    // ---- Concept id normalization (the equivalence rule) --------------------------------------------

    [Theory]
    [InlineData("Command-Line Interface", "Command Line Interface")]
    [InlineData("CancellationToken", "Cancellation Token")]
    [InlineData("Graph Indexing", "graph indexing")]
    [InlineData("Software Development Life-Cycle", "Software Development LifeCycle")]
    public void NearDuplicatePhrasings_CollapseToOneId(string a, string b)
    {
        Assert.Equal(SqliteGraphStorageProvider.ConceptId(a), SqliteGraphStorageProvider.ConceptId(b));
    }

    [Fact]
    public void DistinctConcepts_GetDistinctIds()
    {
        Assert.NotEqual(SqliteGraphStorageProvider.ConceptId("Authentication"), SqliteGraphStorageProvider.ConceptId("Data Access"));
    }

    // ---- Orphan pruning -----------------------------------------------------------------------------

    [Fact]
    public async Task PruneOrphanConcepts_RemovesUnreferenced_KeepsReferenced()
    {
        using var storage = new SqliteGraphStorageProvider(NewDbPath());
        await storage.InitializeAsync();

        await storage.UpsertNodesAsync(new[]
        {
            Code("Live"),
            new GraphNode { Id = "concept_referenced", Name = "Referenced", Type = "Concept" },
            new GraphNode { Id = "concept_orphan", Name = "Orphan", Type = "Concept" }
        });
        // Only "Referenced" has an incoming RELATES_TO from live code.
        await storage.UpsertEdgesAsync(new[]
        {
            new GraphEdge { SourceId = "Live", TargetId = "concept_referenced", Relationship = "RELATES_TO" }
        });

        var pruned = await storage.PruneOrphanConceptsAsync();

        Assert.Equal(1, pruned);
        Assert.Null(await storage.GetNodeByIdAsync("concept_orphan"));
        Assert.NotNull(await storage.GetNodeByIdAsync("concept_referenced"));
    }

    [Fact]
    public async Task DeletingTheReferencingFile_OrphansTheConcept_ThenPruneRemovesIt()
    {
        using var storage = new SqliteGraphStorageProvider(NewDbPath());
        await storage.InitializeAsync();

        // A concept extracted from exactly one code node (the enrichment path).
        await storage.UpsertNodesAsync(new[] { Code("Widget") });
        await storage.UpdateNodeSemanticDataAsync("Widget",
            new SemanticAnalysisResult { Summary = "A widget.", ExtractedConcepts = new() { "Rendering" } });

        var conceptId = SqliteGraphStorageProvider.ConceptId("Rendering");
        Assert.NotNull(await storage.GetNodeByIdAsync(conceptId));            // concept created + linked
        Assert.Equal(0, await storage.PruneOrphanConceptsAsync());           // still referenced → survives

        // The file is deleted/re-indexed: the code node and its RELATES_TO edge go, the concept lingers.
        await storage.ClearFileForReindexAsync("Widget.cs");
        Assert.NotNull(await storage.GetNodeByIdAsync(conceptId));           // orphaned but still present

        Assert.Equal(1, await storage.PruneOrphanConceptsAsync());          // now pruned as stale
        Assert.Null(await storage.GetNodeByIdAsync(conceptId));
    }

    [Fact]
    public async Task ReEnrichment_WithNearDuplicatePhrasings_DoesNotCreateDuplicateConceptNodes()
    {
        using var storage = new SqliteGraphStorageProvider(NewDbPath());
        await storage.InitializeAsync();
        await storage.UpsertNodesAsync(new[] { Code("A"), Code("B") });

        // Two different code nodes extract the SAME concept phrased differently.
        await storage.UpdateNodeSemanticDataAsync("A",
            new SemanticAnalysisResult { Summary = "a", ExtractedConcepts = new() { "Command-Line Interface" } });
        await storage.UpdateNodeSemanticDataAsync("B",
            new SemanticAnalysisResult { Summary = "b", ExtractedConcepts = new() { "Command Line Interface" } });

        var concepts = (await storage.GetNodesByTypesAsync(new[] { "Concept" }));
        Assert.Single(concepts); // one canonical concept node, not two near-duplicates
    }
}
