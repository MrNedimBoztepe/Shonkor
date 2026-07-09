// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// TICKET-207: the trust tier must be honest — no heuristic/LLM write path may persist
/// <see cref="Provenance.Extracted"/>. Only deterministic sources (Roslyn semantics, structural
/// membership) are Extracted; name-fallback, regex parsers and LLM concept links are Inferred/Ambiguous.
/// </summary>
public class ProvenanceIntegrityTests
{
    // Relationships that MAY legitimately be Extracted: compiler-proven Roslyn edges + structural membership.
    private static readonly HashSet<string> ExtractedEligible = new(StringComparer.Ordinal)
    {
        "CONTAINS", "DEFINED_IN",
        "IMPLEMENTS", "EXTENDS", "REFERENCES_TYPE", "CALLS", "INSTANTIATES", "OVERRIDES", "IMPLEMENTS_MEMBER"
    };

    private static async Task<SqliteGraphStorageProvider> LinkAsync(params (string Path, string Code)[] files)
    {
        var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();
        var parser = new RoslynAstParser();
        foreach (var (path, code) in files)
        {
            var (nodes, edges) = await parser.ParseAsync(path, code);
            await storage.UpsertNodesAsync(nodes);
            await storage.UpsertEdgesAsync(edges);
        }
        var compilation = RoslynSemantics.BuildCompilation(files);
        await SemanticCsharpLinker.LinkAsync(storage, compilation);
        return storage;
    }

    // ---------- Semantic linker: resolved = Extracted, name-fallback = Inferred/Ambiguous ----------

    [Fact]
    public async Task ResolvedReference_IsExtracted()
    {
        using var storage = await LinkAsync(
            ("/r/Thing.cs", "namespace A { public class Thing { } }"),
            ("/r/User.cs",  "using A; namespace U { public class User { public Thing F; } }"));

        var (edges, _) = await storage.GetIncidentEdgesAsync("/r/Thing.cs::A.Thing");
        var refEdge = Assert.Single(edges, e => e.Relationship == "REFERENCES_TYPE" && e.SourceId == "/r/User.cs::U.User");
        Assert.Equal(Provenance.Extracted, refEdge.Provenance);
    }

    [Fact]
    public async Task NameFallback_UniqueCandidate_IsInferred()
    {
        // The referenced type's node is in the graph but its source is NOT in the compilation (partial
        // checkout), so Roslyn can't resolve it → name-based fallback, single candidate → Inferred.
        var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();
        await storage.UpsertNodesAsync(new[]
        {
            new GraphNode { Id = "/r/Widget.cs::N.Widget", Name = "Widget", Type = "Class", FilePath = "/r/Widget.cs" }
        });

        var user = ("/r/User.cs", "namespace U { public class User { public Widget W; } }");
        var (nodes, edges) = await new RoslynAstParser().ParseAsync(user.Item1, user.Item2);
        await storage.UpsertNodesAsync(nodes);
        await storage.UpsertEdgesAsync(edges);
        await SemanticCsharpLinker.LinkAsync(storage, RoslynSemantics.BuildCompilation(new[] { user }));

        var (wEdges, _) = await storage.GetIncidentEdgesAsync("/r/Widget.cs::N.Widget");
        var edge = Assert.Single(wEdges, e => e.Relationship == "REFERENCES_TYPE");
        Assert.Equal(Provenance.Inferred, edge.Provenance);
    }

    [Fact]
    public async Task NameFallback_MultipleCandidates_IsAmbiguous()
    {
        var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();
        // Two same-named Widget nodes not in the compilation → fallback resolves to BOTH → Ambiguous.
        await storage.UpsertNodesAsync(new[]
        {
            new GraphNode { Id = "/r/A.cs::A.Widget", Name = "Widget", Type = "Class", FilePath = "/r/A.cs" },
            new GraphNode { Id = "/r/B.cs::B.Widget", Name = "Widget", Type = "Class", FilePath = "/r/B.cs" }
        });

        var user = ("/r/User.cs", "namespace U { public class User { public Widget W; } }");
        var (nodes, edges) = await new RoslynAstParser().ParseAsync(user.Item1, user.Item2);
        await storage.UpsertNodesAsync(nodes);
        await storage.UpsertEdgesAsync(edges);
        await SemanticCsharpLinker.LinkAsync(storage, RoslynSemantics.BuildCompilation(new[] { user }));

        var (aEdges, _) = await storage.GetIncidentEdgesAsync("/r/A.cs::A.Widget");
        var edge = Assert.Single(aEdges, e => e.Relationship == "REFERENCES_TYPE");
        Assert.Equal(Provenance.Ambiguous, edge.Provenance);
    }

    // ---------- Edge upsert: MIN provenance (trust can be upgraded, never frozen stale) ----------

    [Fact]
    public async Task EdgeUpsert_MinProvenance_UpgradesTrust_ButNeverDowngrades()
    {
        using var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();
        await storage.UpsertNodesAsync(new[]
        {
            new GraphNode { Id = "a", Name = "A", Type = "Class" },
            new GraphNode { Id = "b", Name = "B", Type = "Class" }
        });

        // Inferred first, then Extracted → upgrades to Extracted.
        await storage.UpsertEdgesAsync(new[] { new GraphEdge { SourceId = "a", TargetId = "b", Relationship = "REFERENCES_TYPE", Provenance = Provenance.Inferred } });
        await storage.UpsertEdgesAsync(new[] { new GraphEdge { SourceId = "a", TargetId = "b", Relationship = "REFERENCES_TYPE", Provenance = Provenance.Extracted } });
        var (edges1, _) = await storage.GetIncidentEdgesAsync("a");
        Assert.Equal(Provenance.Extracted, Assert.Single(edges1, e => e.Relationship == "REFERENCES_TYPE").Provenance);

        // A later Inferred re-scan must NOT downgrade the proven edge.
        await storage.UpsertEdgesAsync(new[] { new GraphEdge { SourceId = "a", TargetId = "b", Relationship = "REFERENCES_TYPE", Provenance = Provenance.Ambiguous } });
        var (edges2, _) = await storage.GetIncidentEdgesAsync("a");
        Assert.Equal(Provenance.Extracted, Assert.Single(edges2, e => e.Relationship == "REFERENCES_TYPE").Provenance);
    }

    [Fact]
    public async Task EdgeProperties_ArePersisted_AndMaterializedOnRead()
    {
        using var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();
        await storage.UpsertNodesAsync(new[]
        {
            new GraphNode { Id = "a", Name = "A", Type = "Class" },
            new GraphNode { Id = "b", Name = "B", Type = "Class" }
        });
        await storage.UpsertEdgesAsync(new[]
        {
            new GraphEdge { SourceId = "a", TargetId = "b", Relationship = "EXTENDS",
                Properties = new() { ["source"] = "metadata", ["note"] = "x" } }
        });

        var all = await storage.GetAllEdgesAsync();
        var edge = Assert.Single(all, e => e.Relationship == "EXTENDS");
        Assert.Equal("metadata", edge.Properties.GetValueOrDefault("source"));
        Assert.Equal("x", edge.Properties.GetValueOrDefault("note"));
    }

    // ---------- Scanner: heuristic parsers are Inferred; structural membership stays Extracted ----------

    [Fact]
    public async Task GraphQlParser_StructuralDefinedIn_StaysExtracted_DespiteInferredDefault()
    {
        // GraphQLParser.DefaultProvenance is Inferred (regex-based), but its only edges are structural
        // DEFINED_IN — which the scanner's structural-edge exemption keeps Extracted.
        var dir = Path.Combine(Path.GetTempPath(), $"shonkor_prov_gql_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "q.graphql"),
                "query GetPromo { item { ...on Promo { title } } }");
            using var storage = new SqliteGraphStorageProvider(":memory:");
            await storage.InitializeAsync();
            var scanner = new GraphIndexScanner(storage, new IFileParser[] { new GraphQLParser() });
            await scanner.ScanDirectoryAsync(dir, Array.Empty<string>());

            var definedIn = (await storage.GetAllEdgesAsync()).Where(e => e.Relationship == "DEFINED_IN").ToList();
            Assert.NotEmpty(definedIn);
            Assert.All(definedIn, e => Assert.Equal(Provenance.Extracted, e.Provenance));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    // ---------- The guard: no Extracted edge outside the whitelist across a mixed-source graph ----------

    [Fact]
    public async Task Guard_NoExtractedEdge_OutsideTheDeterministicWhitelist()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"shonkor_prov_guard_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "Svc.cs"),
                "namespace N { public class Svc { public Helper H; } public class Helper { } }");
            await File.WriteAllTextAsync(Path.Combine(dir, "q.graphql"),
                "query Q { item { ...on Card { id } } }");
            await File.WriteAllTextAsync(Path.Combine(dir, "metadata.php"),
                "<?php $m = ['extend' => ['oxArticle' => 'My\\Article']];");
            await File.WriteAllTextAsync(Path.Combine(dir, "app.js"),
                "import { X } from './other';");
            await File.WriteAllTextAsync(Path.Combine(dir, "doc.md"),
                "# Title\nSee [the guide](./other.md) for details.");

            using var storage = new SqliteGraphStorageProvider(":memory:");
            await storage.InitializeAsync();
            var scanner = new GraphIndexScanner(storage,
                new IFileParser[] { new RoslynAstParser(), new GraphQLParser(), new PhpModuleParser(), new JavaScriptParser(), new MarkdownHierarchyParser() },
                semanticCsharp: true);
            await scanner.ScanDirectoryAsync(dir, Array.Empty<string>());

            var all = await storage.GetAllEdgesAsync();
            var offenders = all
                .Where(e => e.Provenance == Provenance.Extracted && !ExtractedEligible.Contains(e.Relationship))
                .Select(e => $"{e.Relationship} ({e.SourceId} -> {e.TargetId})")
                .Distinct()
                .ToList();

            Assert.True(offenders.Count == 0,
                "heuristic edges must not be Extracted; offenders: " + string.Join("; ", offenders));

            // And the heuristic families are positively Inferred (not merely absent).
            Assert.All(all.Where(e => e.Relationship is "IMPORTS" or "OVERRIDES_BLOCK"),
                e => Assert.NotEqual(Provenance.Extracted, e.Provenance));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }
}
