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

        var (edges, _) = await storage.GetIncidentEdgesAsync("/repo/S.cs::S::Helper");

        Assert.Contains(edges, e => e.SourceId == "/repo/S.cs::S::Run" && e.TargetId == "/repo/S.cs::S::Helper" && e.Relationship == "CALLS");
    }
}
