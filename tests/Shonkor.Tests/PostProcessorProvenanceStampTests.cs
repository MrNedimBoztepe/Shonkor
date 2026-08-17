// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// #400: post-processor edges were upserted RAW, bypassing <c>StampProvenance</c>. An unset
/// <see cref="GraphEdge.Provenance"/> defaults to <see cref="Provenance.Extracted"/>, so a producer that
/// derives links from an assembled graph — inference by definition — was silently claiming compiler-grade
/// trust. Measured on a real Sitecore solution: 87 <c>REFERENCES_ITEM</c> edges at <c>Extracted</c>,
/// produced by a GUID-shaped field value heuristically read as an item link.
///
/// <para>
/// The host now stamps them exactly like parser output, via <see cref="IGraphPostProcessor.DefaultProvenance"/>.
/// These tests pin the three cases that matter: the untagged edge is capped, a self-tagged weaker edge is
/// left alone, and a post-processor that explicitly declares determinism keeps it.
/// </para>
/// </summary>
public class PostProcessorProvenanceStampTests
{
    private const string Rel = "PP_REL";

    /// <summary>A post-processor with a settable baseline that emits one edge with a settable tier.</summary>
    private sealed class StubPostProcessor(Provenance? baseline, Provenance? edgeTier, string relationship = Rel)
        : IGraphPostProcessor
    {
        public string Name => "test.stamp-probe";

        // Omitting the override entirely is the interesting default case, so it is expressed as a null baseline.
        public Provenance DefaultProvenance => baseline ?? Provenance.Inferred;

        public Task<GraphEnrichment> ProcessAsync(IGraphView graph)
        {
            var edge = new GraphEdge { SourceId = "pp::src", TargetId = "pp::tgt", Relationship = relationship };
            if (edgeTier is { } t) edge = edge with { Provenance = t };

            return Task.FromResult(new GraphEnrichment(
                Nodes: new[]
                {
                    new GraphNode { Id = "pp::src", Type = "Stub", Name = "src" },
                    new GraphNode { Id = "pp::tgt", Type = "Stub", Name = "tgt" },
                },
                Edges: new[] { edge },
                Diagnostics: Array.Empty<GraphDiagnostic>()));
        }
    }

    private static async Task<Provenance> TierAfterScanAsync(IGraphPostProcessor pp, string relationship = Rel)
    {
        var dir = Path.Combine(Path.GetTempPath(), "shonkor-ppstamp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "readme.md"), "# Title\n\nBody.\n");

            using var storage = new SqliteGraphStorageProvider(":memory:");
            await storage.InitializeAsync();

            var scanner = new GraphIndexScanner(storage, new IFileParser[] { new MarkdownHierarchyParser() },
                postProcessors: new[] { pp });
            await scanner.ScanDirectoryAsync(dir, Array.Empty<string>());

            // Matched on the source id, not just the relationship: the markdown parser emits CONTAINS of its
            // own, so the structural case would otherwise match two edges.
            var edges = await storage.GetAllEdgesAsync();
            return Assert.Single(edges, e => e.Relationship == relationship && e.SourceId == "pp::src").Provenance;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// The regression itself: a post-processor that does not tag its edge no longer lands as a hard fact.
    /// Before the stamp this asserted <c>Extracted</c> — the whole defect in one line.
    /// </summary>
    [Fact]
    public async Task UntaggedPostProcessorEdge_IsCappedAtInferred_NotExtracted()
    {
        Assert.Equal(Provenance.Inferred, await TierAfterScanAsync(new StubPostProcessor(baseline: null, edgeTier: null)));
    }

    /// <summary>
    /// The stamp only ever RAISES uncertainty, so a post-processor that already rates an individual edge
    /// weaker than its own baseline keeps that rating. This is what leaves <c>ClrTypeResolverPostProcessor</c>
    /// — which assigns Inferred or Ambiguous per candidate count — completely unaffected by the change.
    /// </summary>
    [Fact]
    public async Task SelfTaggedAmbiguousEdge_KeepsItsOwnWeakerTier()
    {
        Assert.Equal(Provenance.Ambiguous,
            await TierAfterScanAsync(new StubPostProcessor(baseline: Provenance.Inferred, edgeTier: Provenance.Ambiguous)));
    }

    /// <summary>
    /// The escape hatch, and the reason the default is a ceiling rather than a fixed value: a post-processor
    /// whose output really is deterministic declares it, and the declaration is honoured. Making that an
    /// explicit act is the point — the claim has to be made, not inherited.
    /// </summary>
    [Fact]
    public async Task PostProcessorDeclaringExtracted_KeepsExtracted()
    {
        Assert.Equal(Provenance.Extracted,
            await TierAfterScanAsync(new StubPostProcessor(baseline: Provenance.Extracted, edgeTier: null)));
    }

    /// <summary>
    /// Structural membership is a deterministic fact ("this node IS in this file"), so <c>StampProvenance</c>
    /// exempts CONTAINS/DEFINED_IN. A post-processor emitting one must not have it downgraded by a baseline
    /// that exists to cap inference.
    /// </summary>
    [Fact]
    public async Task StructuralEdgeFromPostProcessor_IsNotDowngraded()
    {
        Assert.Equal(Provenance.Extracted,
            await TierAfterScanAsync(new StubPostProcessor(baseline: Provenance.Inferred, edgeTier: null, relationship: "CONTAINS"), "CONTAINS"));
    }
}
