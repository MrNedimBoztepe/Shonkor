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
            // #312: JS comes from the plugin's TypeScriptParser now. A bogus NodePath keeps the guard
            // deterministic (Esprima fallback, no Node process) while preserving the Inferred JS IMPORTS edge.
            await using var jsParser = new Shonkor.Plugin.TypeScript.TypeScriptParser(
                new Shonkor.Plugin.TypeScript.SidecarSettings { NodePath = Path.Combine(dir, "no-such-node.exe") });
            var scanner = new GraphIndexScanner(storage,
                new IFileParser[] { new RoslynAstParser(), new GraphQLParser(), new PhpModuleParser(), jsParser, new MarkdownHierarchyParser() },
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

    /// <summary>
    /// #406, the durable form of the guard above. The whitelist check only asks "is anything Extracted that
    /// should not be" — it is blind in the other direction, and it is stated in prose inside a test rather
    /// than as data anything else can use.
    ///
    /// <para>
    /// <see cref="ProvenanceInvariant"/> encodes the full <c>(RelationType, Provenance) -&gt; producer</c>
    /// mapping, so this asserts every edge of a scanned graph sits at a tier its relationship may hold —
    /// not merely that nothing over-claims. The same table drives the repair migration's before/after
    /// counting, which is why it is data and not an assertion.
    /// </para>
    ///
    /// <para>
    /// Deliberately the same multi-producer fixture as the guard above rather than a wider one: what this
    /// pins is the TABLE, and a table that disagrees with the producers fails here regardless of corpus
    /// size. Running it against a real graph is report mode, not a test — a third-party plugin's
    /// relationship is a gap in the table, not a defect, and must not turn someone else's build red.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Guard_EveryEdgeSitsAtATierItsRelationshipMayHold()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"shonkor_prov_table_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "Svc.cs"),
                "namespace N { public interface IThing { void Go(); } "
                + "public class Svc : IThing { public Helper H; public void Go() { var h = new Helper(); h.Use(); } } "
                + "public class Helper { public void Use() { } } "
                + "public class Derived : Svc { } }");
            await File.WriteAllTextAsync(Path.Combine(dir, "q.graphql"),
                "query Q { item { ...on Card { id } } }");
            await File.WriteAllTextAsync(Path.Combine(dir, "metadata.php"),
                "<?php $m = ['extend' => ['oxArticle' => 'My\\Article']];");
            await File.WriteAllTextAsync(Path.Combine(dir, "doc.md"),
                "# Title\nSee [the guide](./other.md) for details.");

            using var storage = new SqliteGraphStorageProvider(":memory:");
            await storage.InitializeAsync();
            var scanner = new GraphIndexScanner(storage,
                new IFileParser[] { new RoslynAstParser(), new GraphQLParser(), new PhpModuleParser(), new MarkdownHierarchyParser() },
                semanticCsharp: true);
            await scanner.ScanDirectoryAsync(dir, Array.Empty<string>());

            var all = await storage.GetAllEdgesAsync();
            Assert.NotEmpty(all); // a table that passes because nothing was scanned proves nothing

            var (violations, unclassified, totalEdges) = ProvenanceInvariant.Check(all);

            Assert.True(violations.Count == 0,
                "an edge holds a tier its relationship may not hold:\n" + ProvenanceInvariant.Report(violations, [], totalEdges));

            // A relationship the first-party producers emit but the table omits is a hole in the table, and
            // this fixture only runs first-party producers — so here it IS a failure.
            Assert.True(unclassified.Count == 0,
                "a first-party relationship is missing from ProvenanceInvariant.Rules:\n"
                + ProvenanceInvariant.Report([], unclassified));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// #402: the heuristic that decides IMPLEMENTS vs EXTENDS is a naming convention — leading <c>I</c> plus
    /// an uppercase second character — so every edge whose KIND it picks must be Inferred. This pins the
    /// mislabel itself rather than only the tier: <c>Payload : IOManager</c> is a class deriving from a
    /// class, and the parser calls it IMPLEMENTS. Which is fine, as long as it says it is guessing.
    /// </summary>
    [Fact]
    public async Task SyntacticHeritage_IsInferred_EvenWhenTheNameHeuristicGetsTheKindWrong()
    {
        var code = """
            namespace N {
                public class IOManager { }                 // a CLASS whose name trips the 'I' heuristic
                public interface Comparable { }            // an INTERFACE the heuristic will miss
                public class Payload : IOManager, Comparable { }
            }
            """;

        var (_, edges) = await new RoslynAstParser().ParseAsync("/r/Payload.cs", code);

        // The heuristic is wrong about BOTH, in opposite directions...
        var wrongWayImplements = Assert.Single(edges, e => e.Relationship == "IMPLEMENTS" && e.TargetId == "IOManager");
        var wrongWayExtends = Assert.Single(edges, e => e.Relationship == "EXTENDS" && e.TargetId == "Comparable");

        // ...and precisely because it can be, neither claims to be a compiler fact.
        Assert.Equal(Provenance.Inferred, wrongWayImplements.Provenance);
        Assert.Equal(Provenance.Inferred, wrongWayExtends.Provenance);

        // Nothing this parser produces from a base list may be Extracted, whatever the names happen to be.
        Assert.All(edges.Where(e => e.Relationship is "IMPLEMENTS" or "EXTENDS"),
            e => Assert.NotEqual(Provenance.Extracted, e.Provenance));
    }

    /// <summary>
    /// #402's real acceptance criterion, and what #399 left open: after this, <c>(RelationType, Provenance)</c>
    /// tells the two producers of IMPLEMENTS/EXTENDS apart. The syntactic edge points at a bare type NAME and
    /// is Inferred; the resolved edge points at a node ID and is Extracted. They never collide in storage,
    /// so the resolved one is not downgraded — both exist, and the tier says which is which.
    /// </summary>
    [Fact]
    public async Task HeritageEdges_TierNowIdentifiesTheProducer()
    {
        using var storage = await LinkAsync(
            ("/r/IThing.cs", "namespace A { public interface IThing { } }"),
            ("/r/Thing.cs", "using A; namespace A { public class Thing : IThing { } }"));

        var heritage = (await storage.GetAllEdgesAsync())
            .Where(e => e.Relationship is "IMPLEMENTS" or "EXTENDS")
            .ToList();
        Assert.NotEmpty(heritage);

        foreach (var edge in heritage)
        {
            // A node id carries the file path and the '::' separator this repo mints ids with; a bare
            // syntactic name never does. That is the observable difference between the two producers.
            var targetIsResolvedNodeId = edge.TargetId.Contains("::", StringComparison.Ordinal);
            Assert.Equal(
                targetIsResolvedNodeId ? Provenance.Extracted : Provenance.Inferred,
                edge.Provenance);
        }

        // Both producers really did run — otherwise the loop above proves nothing.
        Assert.Contains(heritage, e => e.Provenance == Provenance.Extracted);
        Assert.Contains(heritage, e => e.Provenance == Provenance.Inferred);
    }

    /// <summary>
    /// The table has to stay internally consistent: a repair target that is not itself a legitimate tier
    /// would move edges from one violation straight into another, and the migration would never converge.
    /// </summary>
    [Fact]
    public void InvariantTable_RepairTargets_AreThemselvesLegitimate()
    {
        foreach (var rule in ProvenanceInvariant.Rules)
        {
            Assert.False(rule.Legitimate.Count == 0, $"{rule.Relationship} permits no tier at all");
            if (rule.RepairTo is { } target)
            {
                Assert.True(rule.Legitimate.Contains(target),
                    $"{rule.Relationship} repairs to {target}, which it does not itself permit");
            }
        }

        // No duplicate relationships — a second entry would silently shadow the first.
        Assert.Equal(
            ProvenanceInvariant.Rules.Count,
            ProvenanceInvariant.Rules.Select(r => r.Relationship).Distinct(StringComparer.Ordinal).Count());
    }
}
