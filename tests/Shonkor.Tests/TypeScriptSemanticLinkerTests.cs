// Licensed to Shonkor under the MIT License.

using Microsoft.Extensions.Logging;

using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Infrastructure.Services;
using Shonkor.Infrastructure.Storage;
using Shonkor.Plugin.TypeScript;

namespace Shonkor.Tests;

/// <summary>
/// Acceptance-criteria coverage for the #294 TypeScript semantic linker: the whole-program post-processor
/// drives the REAL Node sidecar (one <c>ts.createProgram</c> + type-checker) over a multi-file fixture and
/// must emit CROSS-FILE CALLS / REFERENCES_TYPE / OVERRIDES / IMPLEMENTS_MEMBER + sharpened EXTENDS/IMPLEMENTS
/// edges resolved to NODE IDS. Every test builds the graph exactly as production does — the #293
/// <see cref="TypeScriptParser"/> parses each file, its nodes go into an in-memory <see cref="IGraphView"/>,
/// then the linker runs against it — so the edge ids are asserted against the actual parser node ids.
/// </summary>
public sealed class TypeScriptSemanticLinkerTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    private string NewProjectDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "shonkor-ts-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Parses each file in <paramref name="dir"/> with the real #293 sidecar parser and returns an in-memory
    /// graph view over all resulting nodes — the phase-1 snapshot the post-processor observes in production.
    /// </summary>
    private static async Task<(FakeGraphView Graph, IReadOnlyList<GraphNode> Nodes)> ParseAllAsync(string dir)
    {
        var host = new TestHost();
        var nodes = new List<GraphNode>();
        await using var parser = new TypeScriptParser();
        parser.Initialize(host);

        foreach (var file in Directory.EnumerateFiles(dir).Where(f => f.EndsWith(".ts") || f.EndsWith(".tsx")))
        {
            var content = await File.ReadAllTextAsync(file);
            var (fileNodes, _) = await parser.ParseAsync(file, content);
            nodes.AddRange(fileNodes);
        }

        return (new FakeGraphView(nodes), nodes);
    }

    private static async Task<GraphEnrichment> LinkAsync(FakeGraphView graph)
    {
        var linker = new TypeScriptSemanticLinker();
        linker.Initialize(new TestHost());
        return await linker.ProcessAsync(graph);
    }

    private static async Task WriteTsconfig(string dir) =>
        await File.WriteAllTextAsync(Path.Combine(dir, "tsconfig.json"),
            """{ "compilerOptions": { "target": "ES2022", "module": "ESNext", "moduleResolution": "Bundler", "strict": true } }""");

    // ---- AC#1: cross-file CALLS / REFERENCES_TYPE / OVERRIDES / IMPLEMENTS_MEMBER on node ids ----

    [Fact]
    public async Task WholeProgram_EmitsCrossFileSemanticEdges_OnNodeIds()
    {
        var dir = NewProjectDir();
        await WriteTsconfig(dir);
        var shapePath = Path.Combine(dir, "shape.ts");
        var circlePath = Path.Combine(dir, "circle.ts");
        await File.WriteAllTextAsync(shapePath,
            "export class Shape {\n" +
            "  area(): number { return 0; }\n" +
            "}\n" +
            "export interface Drawable { draw(): void; }\n");
        await File.WriteAllTextAsync(circlePath,
            "import { Shape, Drawable } from './shape';\n" +
            "export class Circle extends Shape implements Drawable {\n" +
            "  draw(): void {}\n" +
            "  area(): number { return 3; }\n" +
            "  compute(s: Shape): number { return s.area(); }\n" +
            "}\n");

        var (graph, _) = await ParseAllAsync(dir);
        var enrichment = await LinkAsync(graph);
        var edges = enrichment.Edges;

        var shapeId = $"{shapePath}::shape::Shape";
        var drawableId = $"{shapePath}::shape::Drawable";
        var circleId = $"{circlePath}::circle::Circle";

        // CALLS: Circle.compute -> Shape.area, cross-file, on the target METHOD node id.
        Assert.Contains(edges, e => e.Relationship == "CALLS"
            && e.SourceId == $"{circlePath}::circle::Circle::compute"
            && e.TargetId == $"{shapePath}::shape::Shape::area");

        // REFERENCES_TYPE: Circle -> Shape (the `s: Shape` param type), cross-file, on the type node id.
        Assert.Contains(edges, e => e.Relationship == "REFERENCES_TYPE"
            && e.SourceId == circleId && e.TargetId == shapeId);

        // OVERRIDES: Circle.area -> Shape.area (method-level override, cross-file).
        Assert.Contains(edges, e => e.Relationship == "OVERRIDES"
            && e.SourceId == $"{circlePath}::circle::Circle::area"
            && e.TargetId == $"{shapePath}::shape::Shape::area");

        // IMPLEMENTS_MEMBER: Circle.draw -> Drawable.draw (interface member satisfied, cross-file).
        Assert.Contains(edges, e => e.Relationship == "IMPLEMENTS_MEMBER"
            && e.SourceId == $"{circlePath}::circle::Circle::draw"
            && e.TargetId == $"{drawableId}::draw");

        // Every edge points at a real, parser-emitted node (no dangling).
        var known = graph.AllNodeIds();
        Assert.All(edges, e =>
        {
            Assert.Contains(e.SourceId, known);
            Assert.Contains(e.TargetId, known);
        });
    }

    // ---- AC#2: same-named types in different modules are told apart by the type checker (not by name) ----

    [Fact]
    public async Task SameNamedTypes_InDifferentModules_ResolveToTheCorrectNode()
    {
        var dir = NewProjectDir();
        await WriteTsconfig(dir);
        var aPath = Path.Combine(dir, "a.ts");
        var bPath = Path.Combine(dir, "b.ts");
        var usePath = Path.Combine(dir, "use.ts");
        await File.WriteAllTextAsync(aPath, "export class Model { a(): void {} }\n");
        await File.WriteAllTextAsync(bPath, "export class Model { b(): void {} }\n");
        await File.WriteAllTextAsync(usePath,
            "import { Model } from './b';\n" +
            "export class Consumer {\n" +
            "  go(m: Model): void { m.b(); }\n" +
            "}\n");

        var (graph, _) = await ParseAllAsync(dir);
        var edges = (await LinkAsync(graph)).Edges;

        // The reference resolves to b.ts's Model (the imported one), NOT a.ts's same-named Model.
        Assert.Contains(edges, e => e.Relationship == "REFERENCES_TYPE"
            && e.SourceId == $"{usePath}::use::Consumer"
            && e.TargetId == $"{bPath}::b::Model");
        Assert.DoesNotContain(edges, e => e.Relationship == "REFERENCES_TYPE"
            && e.TargetId == $"{aPath}::a::Model");

        // Likewise the call lands on b.ts's Model.b, not a.ts (which has no `b`, so a name match could never
        // hit it anyway — this asserts the positive: the checker chose the right module).
        Assert.Contains(edges, e => e.Relationship == "CALLS"
            && e.TargetId == $"{bPath}::b::Model::b");
    }

    // ---- AC#3: cross-file `A extends B` sharpens the syntactic (same-file-only) #293 variant ----

    [Fact]
    public async Task CrossFileExtends_ResolvesToTheRealTargetNode()
    {
        var dir = NewProjectDir();
        await WriteTsconfig(dir);
        var basePath = Path.Combine(dir, "base.ts");
        var derivedPath = Path.Combine(dir, "derived.ts");
        await File.WriteAllTextAsync(basePath, "export class Base {}\n");
        await File.WriteAllTextAsync(derivedPath,
            "import { Base } from './base';\n" +
            "export class Derived extends Base {}\n");

        var (graph, _) = await ParseAllAsync(dir);
        var edges = (await LinkAsync(graph)).Edges;

        // #293 could NOT emit this (base is in another file); the semantic linker resolves it cross-file...
        var extends = Assert.Single(edges, e => e.Relationship == "EXTENDS");
        Assert.Equal($"{derivedPath}::derived::Derived", extends.SourceId);
        Assert.Equal($"{basePath}::base::Base", extends.TargetId);
        // ... and stamps it EXTRACTED (so the MIN-provenance upsert can sharpen a coinciding INFERRED edge).
        Assert.Equal(Provenance.Extracted, extends.Provenance);
    }

    // ---- AC#4: a symbol declared only externally (node_modules / lib) yields no edge (no dangling) ----

    [Fact]
    public async Task ExternalSymbol_ProducesNoEdge_NoDangling()
    {
        var dir = NewProjectDir();
        await WriteTsconfig(dir);
        var path = Path.Combine(dir, "widget.ts");
        // `Array<number>` (lib.es), `console.log` (lib.dom/node) — both external, no indexed node.
        await File.WriteAllTextAsync(path,
            "export class Widget extends Array<number> {\n" +
            "  render(): void { console.log('x'); }\n" +
            "}\n");

        var (graph, _) = await ParseAllAsync(dir);
        var edges = (await LinkAsync(graph)).Edges;

        // No EXTENDS to the external Array, no CALLS to the external console.log.
        Assert.DoesNotContain(edges, e => e.Relationship == "EXTENDS");
        Assert.DoesNotContain(edges, e => e.Relationship == "CALLS");
        // Whatever DID emit points only at real nodes.
        var known = graph.AllNodeIds();
        Assert.All(edges, e => Assert.Contains(e.TargetId, known));
    }

    // ---- AC#5: type-checker-resolved edges are EXTRACTED ----

    [Fact]
    public async Task ResolvedEdges_AreExtracted()
    {
        var dir = NewProjectDir();
        await WriteTsconfig(dir);
        var aPath = Path.Combine(dir, "svc.ts");
        var bPath = Path.Combine(dir, "caller.ts");
        await File.WriteAllTextAsync(aPath, "export class Service { run(): void {} }\n");
        await File.WriteAllTextAsync(bPath,
            "import { Service } from './svc';\n" +
            "export class Caller {\n" +
            "  invoke(s: Service): void { s.run(); }\n" +
            "}\n");

        var (graph, _) = await ParseAllAsync(dir);
        var edges = (await LinkAsync(graph)).Edges;

        Assert.NotEmpty(edges);
        Assert.All(edges, e => Assert.Equal(Provenance.Extracted, e.Provenance));
    }

    // ---- Accessor disambiguation (ratified Ergänzung 1): a get override lands on the base `:get` node ----

    [Fact]
    public async Task AccessorOverride_ResolvesToKindQualifiedNode()
    {
        var dir = NewProjectDir();
        await WriteTsconfig(dir);
        var basePath = Path.Combine(dir, "abase.ts");
        var subPath = Path.Combine(dir, "asub.ts");
        await File.WriteAllTextAsync(basePath,
            "export class ABase {\n" +
            "  get value(): number { return 0; }\n" +
            "  set value(v: number) {}\n" +
            "}\n");
        await File.WriteAllTextAsync(subPath,
            "import { ABase } from './abase';\n" +
            "export class ASub extends ABase {\n" +
            "  get value(): number { return 1; }\n" +
            "}\n");

        var (graph, _) = await ParseAllAsync(dir);
        var edges = (await LinkAsync(graph)).Edges;

        // The getter override lands on the base GETTER node (`:get`), never a bare `::value` (which has no node).
        Assert.Contains(edges, e => e.Relationship == "OVERRIDES"
            && e.SourceId == $"{subPath}::asub::ASub::value:get"
            && e.TargetId == $"{basePath}::abase::ABase::value:get");
        Assert.DoesNotContain(edges, e => e.TargetId == $"{basePath}::abase::ABase::value:set");
    }

    // ---- Untyped JS is not linked here (no EXTRACTED without type info; #293/#295 own it) ----

    [Fact]
    public async Task UntypedJavaScript_IsNotLinked()
    {
        var dir = NewProjectDir();
        // No tsconfig, plain .js — the linker collects only .ts/.tsx JSComponent files, so nothing to link.
        var path = Path.Combine(dir, "plain.js");
        await File.WriteAllTextAsync(path, "export class A { m() {} }\nexport class B extends A {}\n");

        var host = new TestHost();
        var nodes = new List<GraphNode>();
        await using (var parser = new TypeScriptParser())
        {
            parser.Initialize(host);
            var (fileNodes, _) = await parser.ParseAsync(path, await File.ReadAllTextAsync(path));
            nodes.AddRange(fileNodes);
        }

        var graph = new FakeGraphView(nodes);
        var enrichment = await LinkAsync(graph);

        Assert.Empty(enrichment.Edges);
    }

    // ---- #295 AC#3: a union-typed reference resolving to multiple candidates is AMBIGUOUS ----

    [Fact]
    public async Task UnionTypedCall_ResolvingToMultipleCandidates_IsAmbiguous()
    {
        var dir = NewProjectDir();
        await WriteTsconfig(dir);
        var typesPath = Path.Combine(dir, "types.ts");
        var consumerPath = Path.Combine(dir, "consumer.ts");
        await File.WriteAllTextAsync(typesPath,
            "export class A { m(): void {} }\n" +
            "export class B { m(): void {} }\n");
        await File.WriteAllTextAsync(consumerPath,
            "import { A, B } from './types';\n" +
            "export class Consumer {\n" +
            "  union(x: A | B): void { x.m(); }\n" +
            "  single(a: A): void { a.m(); }\n" +
            "}\n");

        var (graph, _) = await ParseAllAsync(dir);
        var edges = (await LinkAsync(graph)).Edges;

        // `x: A | B; x.m()` — the type checker resolves the callee to TWO distinct candidate method nodes.
        // The linker emits ONE CALLS edge per candidate, each flagged ambiguous by the sidecar -> the host
        // stamps AMBIGUOUS: a low-confidence edge to each possibility rather than a false hard link to one.
        var toA = Assert.Single(edges, e => e.Relationship == "CALLS"
            && e.SourceId == $"{consumerPath}::consumer::Consumer::union"
            && e.TargetId == $"{typesPath}::types::A::m");
        var toB = Assert.Single(edges, e => e.Relationship == "CALLS"
            && e.SourceId == $"{consumerPath}::consumer::Consumer::union"
            && e.TargetId == $"{typesPath}::types::B::m");
        Assert.Equal(Provenance.Ambiguous, toA.Provenance);
        Assert.Equal(Provenance.Ambiguous, toB.Provenance);

        // Contrast (pins the trigger): a single-typed callee resolves to exactly ONE candidate -> EXTRACTED.
        var single = Assert.Single(edges, e => e.Relationship == "CALLS"
            && e.SourceId == $"{consumerPath}::consumer::Consumer::single"
            && e.TargetId == $"{typesPath}::types::A::m");
        Assert.Equal(Provenance.Extracted, single.Provenance);
    }

    // ---- #295 MIN-merge: an AMBIGUOUS edge is never silently promoted to EXTRACTED ----

    [Fact]
    public async Task AmbiguousEdge_MinMerge_PinsRatifiedBehaviour()
    {
        // The store keeps the MIN provenance on conflict (AMBIGUOUS=2 > INFERRED=1 > EXTRACTED=0). This pins the
        // #295 safety property: an AMBIGUOUS union edge can never SPONTANEOUSLY become EXTRACTED (2 -> 0 needs an
        // actual proven EXTRACTED input, which the union case never produces). If it merely coincides with a
        // #293 INFERRED heritage edge, INFERRED wins — still not EXTRACTED. Deliberate, not a bug.
        using var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();
        await storage.UpsertNodesAsync(new[]
        {
            new GraphNode { Id = "a", Name = "A", Type = "Class" },
            new GraphNode { Id = "b", Name = "B", Type = "Class" }
        });

        // AMBIGUOUS alone stays AMBIGUOUS (nothing lowers it).
        await storage.UpsertEdgesAsync(new[] { new GraphEdge { SourceId = "a", TargetId = "b", Relationship = "CALLS", Provenance = Provenance.Ambiguous } });
        var (only, _) = await storage.GetIncidentEdgesAsync("a");
        Assert.Equal(Provenance.Ambiguous, Assert.Single(only, e => e.Relationship == "CALLS").Provenance);

        // A coinciding #293 INFERRED edge on the same triple -> MIN(2,1) = INFERRED (never EXTRACTED).
        await storage.UpsertEdgesAsync(new[] { new GraphEdge { SourceId = "a", TargetId = "b", Relationship = "CALLS", Provenance = Provenance.Inferred } });
        var (merged, _) = await storage.GetIncidentEdgesAsync("a");
        Assert.Equal(Provenance.Inferred, Assert.Single(merged, e => e.Relationship == "CALLS").Provenance);
    }

    // ---- #295 AC#2: untyped JS parser edges are INFERRED, never EXTRACTED ----

    [Fact]
    public async Task UntypedJavaScript_ParserEdges_AreInferred_NeverExtracted()
    {
        var dir = NewProjectDir();
        var jsPath = Path.Combine(dir, "plain.js");
        await File.WriteAllTextAsync(jsPath,
            "import { X } from './other';\n" +
            "export class Animal { speak() {} }\n" +
            "export class Dog extends Animal {}\n");

        using var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();
        await using var parser = new TypeScriptParser();
        var scanner = new GraphIndexScanner(storage, new IFileParser[] { parser });
        await scanner.ScanDirectoryAsync(dir, Array.Empty<string>());

        var all = await storage.GetAllEdgesAsync();

        // The #293 same-file heritage the parser emits from untyped JS is purely SYNTACTIC -> INFERRED.
        var extends = Assert.Single(all, e => e.Relationship == "EXTENDS");
        Assert.Equal(Provenance.Inferred, extends.Provenance);

        // No non-structural EXTRACTED edge originates from untyped JS (CONTAINS/DEFINED_IN membership is exempt).
        var structural = new HashSet<string>(StringComparer.Ordinal) { "CONTAINS", "DEFINED_IN" };
        Assert.All(all.Where(e => !structural.Contains(e.Relationship)),
            e => Assert.NotEqual(Provenance.Extracted, e.Provenance));
    }

    // ---- #295 AC#5: integrity invariant across a mixed graph — EXTRACTED lives only in the linker enrichment ----

    [Fact]
    public async Task MixedGraph_OnlyTypedTsLinkerEdgesAreExtracted_IntegrityGuard()
    {
        var dir = NewProjectDir();
        await WriteTsconfig(dir);

        // typed .ts: cross-file heritage (base in another module) — only the whole-program linker can resolve it.
        var basePath = Path.Combine(dir, "base.ts");
        var derivedPath = Path.Combine(dir, "derived.ts");
        await File.WriteAllTextAsync(basePath, "export class Base {}\n");
        await File.WriteAllTextAsync(derivedPath,
            "import { Base } from './base';\n" +
            "export class Derived extends Base {}\n");

        // untyped .js: same-file heritage — must stay INFERRED (the linker never mints EXTRACTED from .js).
        var jsPath = Path.Combine(dir, "legacy.js");
        await File.WriteAllTextAsync(jsPath,
            "export class Animal { speak() {} }\n" +
            "export class Dog extends Animal {}\n");

        using var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();
        await using var parser = new TypeScriptParser();
        var linker = new TypeScriptSemanticLinker();
        linker.Initialize(new TestHost());
        var scanner = new GraphIndexScanner(storage, new IFileParser[] { parser },
            postProcessors: new IGraphPostProcessor[] { linker });
        await scanner.ScanDirectoryAsync(dir, Array.Empty<string>());

        var all = await storage.GetAllEdgesAsync();

        // (a) Whitelist guard (mirrors ProvenanceIntegrityTests): no EXTRACTED edge outside the eligible set.
        var extractedEligible = new HashSet<string>(StringComparer.Ordinal)
        {
            "CONTAINS", "DEFINED_IN",
            "EXTENDS", "IMPLEMENTS", "REFERENCES_TYPE", "CALLS", "OVERRIDES", "IMPLEMENTS_MEMBER"
        };
        var offenders = all
            .Where(e => e.Provenance == Provenance.Extracted && !extractedEligible.Contains(e.Relationship))
            .Select(e => $"{e.Relationship} ({e.SourceId} -> {e.TargetId})")
            .Distinct()
            .ToList();
        Assert.True(offenders.Count == 0,
            "no EXTRACTED edge may exist outside the deterministic whitelist; offenders: " + string.Join("; ", offenders));

        // (b) IMPORTS is a heuristic name-based edge — positively INFERRED, never EXTRACTED.
        var imports = all.Where(e => e.Relationship == "IMPORTS").ToList();
        Assert.NotEmpty(imports);
        Assert.All(imports, e => Assert.NotEqual(Provenance.Extracted, e.Provenance));

        // (c) The untyped-.js same-file EXTENDS stays INFERRED — no EXTRACTED edge originates from .js.
        var jsExtends = Assert.Single(all, e => e.Relationship == "EXTENDS"
            && e.SourceId == $"{jsPath}::legacy::Dog");
        Assert.Equal(Provenance.Inferred, jsExtends.Provenance);

        // (d) The typed-.ts cross-file EXTENDS IS EXTRACTED — proving EXTRACTED lives ONLY in the linker enrichment.
        var tsExtends = Assert.Single(all, e => e.Relationship == "EXTENDS"
            && e.SourceId == $"{derivedPath}::derived::Derived"
            && e.TargetId == $"{basePath}::base::Base");
        Assert.Equal(Provenance.Extracted, tsExtends.Provenance);
    }

    // ---- test doubles ----

    /// <summary>Minimal in-memory <see cref="IGraphView"/> over a fixed node set (edges are irrelevant to the linker).</summary>
    private sealed class FakeGraphView : IGraphView
    {
        private readonly List<GraphNode> _nodes;

        public FakeGraphView(IEnumerable<GraphNode> nodes) => _nodes = nodes.ToList();

        public IReadOnlySet<string> AllNodeIds() => _nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);

        public Task<GraphNode?> GetNodeAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_nodes.FirstOrDefault(n => n.Id == id));

        public Task<IReadOnlyList<GraphNode>> NodesByTypeAsync(string type, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GraphNode>>(_nodes.Where(n => n.Type == type).ToList());

        public Task<IReadOnlyDictionary<string, IReadOnlyList<GraphNode>>> DefinitionsByNameAsync(
            IEnumerable<string> names, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<GraphNode>>>(
                new Dictionary<string, IReadOnlyList<GraphNode>>());

        public Task<(IReadOnlyList<GraphEdge> Edges, IReadOnlyDictionary<string, GraphNode> Neighbours)> IncidentEdgesAsync(
            string nodeId, CancellationToken cancellationToken = default) =>
            Task.FromResult<(IReadOnlyList<GraphEdge>, IReadOnlyDictionary<string, GraphNode>)>(
                (Array.Empty<GraphEdge>(), new Dictionary<string, GraphNode>()));

        public Task<IReadOnlyList<GraphEdge>> EdgesByRelationshipAsync(string relationship, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GraphEdge>>(Array.Empty<GraphEdge>());
    }

    private sealed class TestHost : IPluginHost
    {
        public CapturingLogger Logger { get; } = new();
        ILogger IPluginHost.Logger => Logger;
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly object _gate = new();
        public List<string> Messages { get; } = new();

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate) { Messages.Add(formatter(state, exception)); }
        }
    }
}
