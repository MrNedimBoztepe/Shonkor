// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;
using Shonkor.Infrastructure.Services.Mcp;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// #434: a full reparse of a real solution took <c>RELATES_TO</c> from 28 145 edges to 1 061 — 27 084
/// destroyed, with nothing in the scan's output saying so. The clearing pass drops every edge on a
/// reparsed file's nodes, and the only producer of these edges is the LLM enrichment pass.
///
/// <para>
/// Re-running that pass is not a recovery: asked twice on byte-identical input, the generator reproduced
/// its own concept set for <b>8 of 48</b> nodes. That measurement is why these tests pin "kept", and why
/// the divergence test below pins "reported" rather than "refreshed".
/// </para>
/// </summary>
public sealed class AgentAuthoredEdgeSurvivalTests
{
    private const string File1 = @"C:\repo\Service.cs";
    private const string Anchor = @"C:\repo\Service.cs::Service";
    private const string Concept = "concept_caching";

    private static GraphNode AnchorNode(string hash = "hash-v1") => new()
    {
        Id = Anchor, Type = "Class", Name = "Service", FilePath = File1, Content = "class Service {}", ContentHash = hash
    };

    private static async Task<SqliteGraphStorageProvider> SeededAsync()
    {
        var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();
        await storage.UpsertNodesAsync(new[]
        {
            new GraphNode { Id = File1, Type = "File", Name = "Service.cs", FilePath = File1, ContentHash = "file-v1" },
            AnchorNode(),
            new GraphNode { Id = Concept, Type = "Concept", Name = "Caching" },
            new GraphNode { Id = @"C:\repo\Other.cs::Other", Type = "Class", Name = "Other", FilePath = @"C:\repo\Other.cs" },
        });
        await storage.UpsertEdgesAsync(new[]
        {
            new GraphEdge { SourceId = Anchor, TargetId = Concept, Relationship = "RELATES_TO", Provenance = Provenance.Inferred },
            new GraphEdge { SourceId = Anchor, TargetId = @"C:\repo\Other.cs::Other", Relationship = "CALLS", Provenance = Provenance.Extracted },
        });
        return storage;
    }

    private static async Task<HashSet<string>> RelationshipsAsync(SqliteGraphStorageProvider storage) =>
        (await storage.GetAllEdgesAsync()).Select(e => e.Relationship).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The defect, at the scale it was measured: clearing a file for re-index must not take the assertions
    /// made about it. The extracted edge on the same node still goes — a scan CAN rebuild that one.
    /// </summary>
    [Fact]
    public async Task DeleteByFilePaths_KeepsAgentAuthoredEdges_AndStillDropsExtractedOnes()
    {
        using var storage = await SeededAsync();

        await storage.DeleteByFilePathsAsync(new[] { File1 });

        var kinds = await RelationshipsAsync(storage);
        Assert.Contains("RELATES_TO", kinds);
        Assert.DoesNotContain("CALLS", kinds);
    }

    /// <summary>Same rule on the single-file path, or an incremental edit becomes the cheap way to lose them.</summary>
    [Fact]
    public async Task ClearFileForReindex_KeepsAgentAuthoredEdges_AndStillDropsExtractedOnes()
    {
        using var storage = await SeededAsync();

        await storage.ClearFileForReindexAsync(File1);

        var kinds = await RelationshipsAsync(storage);
        Assert.Contains("RELATES_TO", kinds);
        Assert.DoesNotContain("CALLS", kinds);
    }

    /// <summary>
    /// End to end through the scanner, because that is where the 27 084 were lost — not in a storage call
    /// anyone was watching. A forced reparse of an unchanged tree must leave the assertion standing.
    /// </summary>
    [Fact]
    public async Task ForcedRescan_LeavesAgentAuthoredEdgesStanding()
    {
        var dir = Path.Combine(Path.GetTempPath(), "shonkor-434-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "Service.cs");
            await File.WriteAllTextAsync(file, "public class Service { public void Run() { } }");

            using var storage = new SqliteGraphStorageProvider(":memory:");
            await storage.InitializeAsync();
            var scanner = new GraphIndexScanner(storage, new IFileParser[] { new RoslynAstParser() });
            await scanner.ScanDirectoryAsync(dir, Array.Empty<string>());

            var anchor = (await storage.GetAllNodesAsync()).First(n => n.Type == "Class");
            await storage.UpsertNodesAsync(new[] { new GraphNode { Id = Concept, Type = "Concept", Name = "Caching" } });
            await storage.UpsertEdgesAsync(new[]
            {
                new GraphEdge { SourceId = anchor.Id, TargetId = Concept, Relationship = "RELATES_TO", Provenance = Provenance.Inferred }
            });

            await scanner.ScanDirectoryAsync(dir, Array.Empty<string>(), forceReparse: true);

            Assert.Contains(await storage.GetAllEdgesAsync(), e => e.Relationship == "RELATES_TO" && e.TargetId == Concept);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// The stamp is written by the producer, from the result rather than from the caller's belief about
    /// it — and an unchanged anchor reads back as not diverged.
    /// </summary>
    [Fact]
    public async Task EnrichmentWritesTheSourceStamp_AndAnUnchangedAnchorIsNotDiverged()
    {
        using var storage = await SeededAsync();

        await storage.UpdateNodeSemanticDataAsync(
            Anchor,
            new SemanticAnalysisResult { Summary = "caches things", ExtractedConcepts = { "Caching" }, Model = "qwen2.5-coder" });

        var edge = Assert.Single(await storage.GetAllEdgesAsync(), e => e.Relationship == "RELATES_TO");
        Assert.Equal("qwen2.5-coder", edge.Properties[SourceStateStamp.ModelKey]);
        Assert.False(string.IsNullOrWhiteSpace(edge.Properties[SourceStateStamp.StateKey]));
        Assert.Equal(false, SourceStateStamp.IsDiverged(edge.Properties, "hash-v1"));
    }

    /// <summary>
    /// The point of the whole exercise: when the anchor moves on, the edge SAYS SO and stays. Nothing
    /// re-derives it, because re-deriving would replace one sample with another and call it fresh.
    /// </summary>
    [Fact]
    public async Task ChangedAnchor_IsReportedAsDiverged_AndTheEdgeIsStillThere()
    {
        using var storage = await SeededAsync();
        await storage.UpdateNodeSemanticDataAsync(
            Anchor,
            new SemanticAnalysisResult { Summary = "caches things", ExtractedConcepts = { "Caching" }, Model = "qwen2.5-coder" });

        // The file is edited and re-indexed: same node id, new content hash.
        await storage.UpsertNodesAsync(new[] { AnchorNode("hash-v2") });

        var edge = Assert.Single(await storage.GetAllEdgesAsync(), e => e.Relationship == "RELATES_TO");
        Assert.Equal(true, SourceStateStamp.IsDiverged(edge.Properties, "hash-v2"));
        Assert.Contains("[stale-anchor]", McpToolHelpers.ProvenanceTag(edge, AnchorNode("hash-v2")));
        Assert.DoesNotContain("[stale-anchor]", McpToolHelpers.ProvenanceTag(edge, AnchorNode()));
    }

    /// <summary>
    /// An edge from before the stamp existed must render as neither current nor diverged. "No evidence"
    /// silently reading as "fine" is the mistake that produced 699 wrongly-Extracted edges in the first
    /// place; it does not get to reappear here under a new name.
    /// </summary>
    [Fact]
    public async Task UnstampedEdge_GetsNoMarker_AndIsNotClaimedToBeCurrent()
    {
        using var storage = await SeededAsync();

        var edge = Assert.Single(await storage.GetAllEdgesAsync(), e => e.Relationship == "RELATES_TO");
        Assert.Empty(edge.Properties);
        Assert.Null(SourceStateStamp.IsDiverged(edge.Properties, "hash-v1"));
        Assert.Equal("[inferred]", McpToolHelpers.ProvenanceTag(edge, AnchorNode()));
    }

    /// <summary>
    /// Surviving the clearing pass must not become a licence to dangle. A re-index and a deletion look
    /// identical while the file is being cleared and are trivially distinguishable afterwards, so the
    /// distinction is made in the prune: an assertion whose anchor came back stands, one whose anchor is
    /// gone for good is dropped rather than left pointing at nothing (#436's failure mode).
    /// </summary>
    [Fact]
    public async Task WhenTheAnchorNeverComesBack_ThePruneDropsTheEdge_NotJustTheConcept()
    {
        using var storage = await SeededAsync();

        await storage.ClearFileForReindexAsync(File1);
        Assert.Contains(await storage.GetAllEdgesAsync(), e => e.Relationship == "RELATES_TO"); // survived clearing

        // No re-index follows: the file is gone for good.
        Assert.Equal(1, await storage.PruneOrphanConceptsAsync());

        Assert.DoesNotContain(await storage.GetAllEdgesAsync(), e => e.Relationship == "RELATES_TO");
        Assert.Null(await storage.GetNodeByIdAsync(Concept));
    }

    /// <summary>The other half of the same rule: an anchor that DID come back keeps its assertion.</summary>
    [Fact]
    public async Task WhenTheAnchorComesBack_ThePruneLeavesEverythingStanding()
    {
        using var storage = await SeededAsync();

        await storage.ClearFileForReindexAsync(File1);
        await storage.UpsertNodesAsync(new[] { AnchorNode("hash-v2") }); // re-indexed under the same id

        Assert.Equal(0, await storage.PruneOrphanConceptsAsync());
        Assert.Contains(await storage.GetAllEdgesAsync(), e => e.Relationship == "RELATES_TO");
        Assert.NotNull(await storage.GetNodeByIdAsync(Concept));
    }

    /// <summary>An extracted edge never carries the marker, whatever its properties say.</summary>
    [Fact]
    public void ExtractedRelationships_AreNeverMarked()
    {
        var edge = new GraphEdge
        {
            SourceId = Anchor, TargetId = "x", Relationship = "CALLS", Provenance = Provenance.Extracted,
            Properties = { [SourceStateStamp.StateKey] = "whatever", [SourceStateStamp.ModelKey] = "m" }
        };

        Assert.Equal("[extracted]", McpToolHelpers.ProvenanceTag(edge, AnchorNode("something-else")));
    }
}
