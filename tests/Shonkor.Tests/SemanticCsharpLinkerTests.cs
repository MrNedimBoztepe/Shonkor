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
    public async Task SameArityOverloads_Collide_DocumentedV1Limit()
    {
        // Bar(int) and Bar(string) share arity 1 -> same id. This is the accepted v1 limitation
        // (arity is the only discriminator the parser and the linker can produce identically);
        // full precision is the Phase 2 follow-up. See docs/projects/method-node-id-overloads.md.
        using var storage = await LinkAsync(
            ("/repo/C.cs",
             "namespace N { public class C { " +
             "public void Bar(int x) { } " +
             "public void Bar(string s) { } } }"));

        // Only one node exists for both same-arity overloads (they collapsed onto Bar#1).
        Assert.NotNull(await storage.GetNodeByIdAsync("/repo/C.cs::C::Bar#1"));
        var (allNodes, _) = await storage.GetSubgraphAsync(new[] { "/repo/C.cs::C" }, 1);
        Assert.Single(allNodes, n => n.Type == "Method" && n.Name == "Bar");
    }
}
