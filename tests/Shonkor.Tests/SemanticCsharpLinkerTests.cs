// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Models;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// Tests the semantic C# linker: it emits IMPLEMENTS/EXTENDS/REFERENCES_TYPE/CALLS edges resolved to the
/// EXACT symbol (where name matching is ambiguous), pointing at the parser's node ids.
/// </summary>
public class SemanticCsharpLinkerTests
{
    /// <summary>Parses + upserts the parser nodes for each file, then runs the semantic linker over them.</summary>
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

    [Fact]
    public async Task ReferencesType_ResolvesToTheCorrectFile_AcrossSameNamedTypes()
    {
        using var storage = await LinkAsync(
            ("/repo/AThing.cs", "namespace A { public class Thing { } }"),
            ("/repo/BThing.cs", "namespace B { public class Thing { } }"),
            ("/repo/User.cs",   "using A; namespace U { public class User { public Thing Field; } }"));

        var userId = "/repo/User.cs::User";

        // User -> A.Thing (the imported one), NOT B.Thing — name matching couldn't disambiguate.
        var (aEdges, _) = await storage.GetIncidentEdgesAsync("/repo/AThing.cs::Thing");
        Assert.Contains(aEdges, e => e.SourceId == userId && e.Relationship == "REFERENCES_TYPE");

        var (bEdges, _) = await storage.GetIncidentEdgesAsync("/repo/BThing.cs::Thing");
        Assert.DoesNotContain(bEdges, e => e.SourceId == userId);
    }

    [Fact]
    public async Task UnresolvedReference_FallsBackToNameBasedEdge_SoSemanticModeIsNonLossy()
    {
        // Simulates a partial/non-compiling checkout: the referenced type's source is NOT in the
        // compilation (so Roslyn can't resolve the symbol), but its node IS in the graph. The linker must
        // fall back to a name-based REFERENCES_TYPE edge instead of silently dropping it (TICKET-004).
        var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();

        var parser = new RoslynAstParser();
        // Both files parsed + upserted as nodes...
        foreach (var (path, code) in new[]
        {
            ("/repo/Widget.cs", "namespace A { public class Widget { } }"),
            ("/repo/Consumer.cs", "namespace U { public class Consumer { public Widget W; } }"),
        })
        {
            var (nodes, edges) = await parser.ParseAsync(path, code);
            await storage.UpsertNodesAsync(nodes);
            await storage.UpsertEdgesAsync(edges);
        }

        // ...but the compilation covers ONLY Consumer.cs, so `Widget` is an unresolved symbol.
        var compilation = RoslynSemantics.BuildCompilation(new[]
        {
            ("/repo/Consumer.cs", "namespace U { public class Consumer { public Widget W; } }")
        });
        await SemanticCsharpLinker.LinkAsync(storage, compilation);

        var (edges2, _) = await storage.GetIncidentEdgesAsync("/repo/Widget.cs::Widget");
        Assert.Contains(edges2, e => e.SourceId == "/repo/Consumer.cs::Consumer" && e.Relationship == "REFERENCES_TYPE");

        storage.Dispose();
    }

    [Fact]
    public async Task BaseTypes_EmitImplementsAndExtends_ToNodeIds()
    {
        using var storage = await LinkAsync(
            ("/repo/IBar.cs",  "namespace N { public interface IBar { } }"),
            ("/repo/Base.cs",  "namespace N { public class Base { } }"),
            ("/repo/C.cs",     "using N; namespace N2 { public class C : Base, IBar { } }"));

        var (edges, _) = await storage.GetIncidentEdgesAsync("/repo/C.cs::C");

        Assert.Contains(edges, e => e.SourceId == "/repo/C.cs::C" && e.TargetId == "/repo/IBar.cs::IBar" && e.Relationship == "IMPLEMENTS");
        Assert.Contains(edges, e => e.SourceId == "/repo/C.cs::C" && e.TargetId == "/repo/Base.cs::Base" && e.Relationship == "EXTENDS");
    }

    [Fact]
    public async Task Invocation_EmitsCallsEdge_CallerToCallee()
    {
        using var storage = await LinkAsync(
            ("/repo/S.cs", "namespace N { public class S { public void Run() { Helper(); } public void Helper() { } } }"));

        var (edges, _) = await storage.GetIncidentEdgesAsync("/repo/S.cs::S::Helper#0");

        Assert.Contains(edges, e => e.SourceId == "/repo/S.cs::S::Run#0" && e.TargetId == "/repo/S.cs::S::Helper#0" && e.Relationship == "CALLS");
    }

    [Fact]
    public async Task ExtensionMethod_CallsEdge_ResolvesToUnreducedArity()
    {
        // `s.Use()` resolves to the REDUCED extension symbol (0 params), but the parser counted the `this`
        // parameter (arity 1 -> node id ::Ext::Use#1). Without unreducing, the CALLS edge would dangle at #0.
        using var storage = await LinkAsync(
            ("/repo/Ext.cs", "namespace N { public static class Ext { public static void Use(this string s) { } } }"),
            ("/repo/C.cs",   "namespace N { public class C { public void Run() { \"x\".Use(); } } }"));

        var (edges, _) = await storage.GetIncidentEdgesAsync("/repo/Ext.cs::Ext::Use#1");
        Assert.Contains(edges, e => e.SourceId == "/repo/C.cs::C::Run#0" && e.Relationship == "CALLS");
    }

    [Fact]
    public async Task DifferentArityOverloads_GetDistinctNodes_AndCallsResolveToTheRightOne()
    {
        // Foo() and Foo(int) are distinct overloads (arity 0 vs 1). Run calls the one-arg overload;
        // the CALLS edge must point at Foo#1, and both overloads must exist as separate nodes.
        using var storage = await LinkAsync(
            ("/repo/O.cs",
             "namespace N { public class O { " +
             "public void Foo() { } " +
             "public void Foo(int x) { } " +
             "public void Run() { Foo(1); } } }"));

        // Both overloads survived as distinct nodes (no INSERT-OR-REPLACE collision).
        Assert.NotNull(await storage.GetNodeByIdAsync("/repo/O.cs::O::Foo#0"));
        Assert.NotNull(await storage.GetNodeByIdAsync("/repo/O.cs::O::Foo#1"));

        // The call resolves to the one-arg overload, not the zero-arg one.
        var (oneArg, _) = await storage.GetIncidentEdgesAsync("/repo/O.cs::O::Foo#1");
        Assert.Contains(oneArg, e => e.SourceId == "/repo/O.cs::O::Run#0" && e.Relationship == "CALLS");

        var (zeroArg, _) = await storage.GetIncidentEdgesAsync("/repo/O.cs::O::Foo#0");
        Assert.DoesNotContain(zeroArg, e => e.SourceId == "/repo/O.cs::O::Run#0" && e.Relationship == "CALLS");
    }

    [Fact]
    public async Task SameArityOverloads_GetDistinctNodes_AndCallResolvesToTheRightOne()
    {
        // Bar(int) and Bar(string) share arity 1; Phase 2 disambiguates them by declaration span, so they
        // are distinct nodes and a call to Bar(1) resolves to the int overload (not the string one).
        using var storage = await LinkAsync(
            ("/repo/C.cs",
             "namespace N { public class C { " +
             "public void Bar(int x) { } " +
             "public void Bar(string s) { } " +
             "public void Run() { Bar(1); } } }"));

        // Two distinct method nodes named "Bar" survived (no same-arity collapse).
        var (typeSubgraph, _) = await storage.GetSubgraphAsync(new[] { "/repo/C.cs::C" }, 1);
        var barNodes = typeSubgraph.Where(n => n.Type == "Method" && n.Name == "Bar").ToList();
        Assert.Equal(2, barNodes.Count);

        // Exactly one CALLS edge from Run, and it targets the int overload.
        var (cNodes, cEdges) = await storage.GetSubgraphAsync(new[] { "/repo/C.cs::C" }, 2);
        var calls = cEdges.Where(e => e.Relationship == "CALLS" && e.SourceId.Contains("::Run#")).ToList();
        Assert.Single(calls);
        var calleeId = calls[0].TargetId;
        var callee = await storage.GetNodeByIdAsync(calleeId);
        Assert.NotNull(callee);
        Assert.Contains("int", callee!.Properties.GetValueOrDefault("parameters", ""));
    }

    [Fact]
    public async Task OverrideMethod_EmitsOverridesEdge_ToBaseMethod()
    {
        // Method-level override chain (beyond the type-level EXTENDS): Derived.Do overrides Base.Do.
        using var storage = await LinkAsync(
            ("/repo/Base.cs", "namespace N { public class Base { public virtual void Do() { } } }"),
            ("/repo/Derived.cs", "namespace N { public class Derived : Base { public override void Do() { } } }"));

        var (edges, _) = await storage.GetIncidentEdgesAsync("/repo/Base.cs::Base::Do#0");
        var overrideEdge = edges.SingleOrDefault(e => e.Relationship == "OVERRIDES");
        Assert.NotNull(overrideEdge);
        Assert.Equal("/repo/Derived.cs::Derived::Do#0", overrideEdge!.SourceId);
        Assert.Equal("/repo/Base.cs::Base::Do#0", overrideEdge.TargetId);
        // Semantic-resolved → EXTRACTED provenance.
        Assert.Equal(Provenance.Extracted, overrideEdge.Provenance);
    }

    [Fact]
    public async Task ObjectCreation_EmitsInstantiatesEdge_FromMethodToConstructedType()
    {
        // `new Foo()` in Make() → a method-level INSTANTIATES edge to the Foo type (distinct from the
        // type-level REFERENCES_TYPE the return-type/usage also produces).
        using var storage = await LinkAsync(
            ("/repo/Foo.cs", "namespace N { public class Foo { } }"),
            ("/repo/Maker.cs", "namespace N { public class Maker { public Foo Make() { return new Foo(); } } }"));

        var (edges, _) = await storage.GetIncidentEdgesAsync("/repo/Foo.cs::Foo");
        var inst = edges.SingleOrDefault(e => e.Relationship == "INSTANTIATES");
        Assert.NotNull(inst);
        Assert.Equal("/repo/Maker.cs::Maker::Make#0", inst!.SourceId);
        Assert.Equal("/repo/Foo.cs::Foo", inst.TargetId);
        Assert.Equal(Provenance.Extracted, inst.Provenance); // semantic-resolved
    }

    [Fact]
    public async Task InterfaceImplementation_EmitsImplementsMemberEdge_AlongsideTypeLevelImplements()
    {
        using var storage = await LinkAsync(
            ("/repo/IFoo.cs", "namespace N { public interface IFoo { void Bar(); } }"),
            ("/repo/C.cs", "namespace N { public class C : IFoo { public void Bar() { } } }"));

        // Type-level IMPLEMENTS still holds …
        var (typeEdges, _) = await storage.GetIncidentEdgesAsync("/repo/IFoo.cs::IFoo");
        Assert.Contains(typeEdges, e => e.SourceId == "/repo/C.cs::C" && e.Relationship == "IMPLEMENTS");

        // … plus the new member-level edge: C.Bar implements IFoo.Bar.
        var (memberEdges, _) = await storage.GetIncidentEdgesAsync("/repo/IFoo.cs::IFoo::Bar#0");
        Assert.Contains(memberEdges, e => e.SourceId == "/repo/C.cs::C::Bar#0"
            && e.TargetId == "/repo/IFoo.cs::IFoo::Bar#0" && e.Relationship == "IMPLEMENTS_MEMBER");
    }
}
