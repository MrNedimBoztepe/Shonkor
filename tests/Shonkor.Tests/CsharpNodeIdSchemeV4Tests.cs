// Licensed to Shonkor under the MIT License.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Shonkor.Core.Services;

namespace Shonkor.Tests;

/// <summary>
/// Regression tests for BUG-014 (node-id scheme v4: same-named types in one file no longer merge),
/// BUG-004 (StartLine/EndLine are 1-based, matching the GraphNode contract) and BUG-034 (record
/// primary constructors produce no dangling ids).
/// </summary>
public class CsharpNodeIdSchemeV4Tests
{
    private const string Path = "/repo/F.cs";

    private static async Task<IReadOnlyList<Core.Models.GraphNode>> ParseAsync(string code)
    {
        var (nodes, _) = await new RoslynAstParser().ParseAsync(Path, code);
        return nodes;
    }

    [Fact]
    public async Task SameNamedTypes_InDifferentNamespaces_GetDistinctNodes()
    {
        var nodes = await ParseAsync("namespace A { public class Foo { } } namespace B { public class Foo { } }");

        var foos = nodes.Where(n => n.Name == "Foo").Select(n => n.Id).ToList();
        Assert.Equal(2, foos.Count);
        Assert.Contains($"{Path}::A.Foo", foos);
        Assert.Contains($"{Path}::B.Foo", foos);
    }

    [Fact]
    public async Task GenericAndNonGeneric_SameName_GetDistinctNodes()
    {
        var nodes = await ParseAsync("namespace N { public class Foo { } public class Foo<T> { } }");

        var foos = nodes.Where(n => n.Name == "Foo").Select(n => n.Id).ToList();
        Assert.Equal(2, foos.Count);
        Assert.Contains($"{Path}::N.Foo", foos);
        Assert.Contains($"{Path}::N.Foo`1", foos);
    }

    [Fact]
    public async Task SameNamedNestedTypes_UnderDifferentParents_GetDistinctNodes_AndDistinctMembers()
    {
        var nodes = await ParseAsync(
            "namespace N { public class A { public class Builder { public void Build() { } } } " +
            "public class B { public class Builder { public void Build() { } } } }");

        var builders = nodes.Where(n => n.Name == "Builder").Select(n => n.Id).ToList();
        Assert.Equal(2, builders.Count);
        Assert.Contains($"{Path}::N.A+Builder", builders);
        Assert.Contains($"{Path}::N.B+Builder", builders);

        // Members collide transitively under the old scheme — must be distinct too.
        var builds = nodes.Where(n => n.Name == "Build").Select(n => n.Id).ToList();
        Assert.Equal(2, builds.Count);
        Assert.Contains($"{Path}::N.A+Builder::Build#0", builds);
        Assert.Contains($"{Path}::N.B+Builder::Build#0", builds);
    }

    [Fact]
    public async Task Lines_Are1Based_AndMembersCarryEndLine()
    {
        // Line 1: namespace; line 2: class; line 3: method; line 5: property; line 6: enum.
        var nodes = await ParseAsync(
            "namespace N;\n" +                    // 1
            "public class C\n{\n" +               // 2-3
            "    public void M()\n    {\n    }\n" + // 4-6
            "    public int P { get; set; }\n" +  // 7
            "}\n" +                               // 8
            "public enum E { A }\n");             // 9

        var c = nodes.Single(n => n.Name == "C");
        Assert.Equal(2, c.StartLine);
        Assert.Equal(8, c.EndLine);

        var m = nodes.Single(n => n.Name == "M");
        Assert.Equal(4, m.StartLine);
        Assert.Equal(6, m.EndLine);

        var p = nodes.Single(n => n.Name == "P");
        Assert.Equal(7, p.StartLine);
        Assert.Equal(7, p.EndLine);

        var e = nodes.Single(n => n.Name == "E");
        Assert.Equal(9, e.StartLine);
        Assert.Equal(9, e.EndLine);
    }

    [Fact]
    public async Task ParserAndSemantics_AgreeOnIds_ForNamespacedNestedGenerics()
    {
        const string code = "namespace My.App { public class Outer { public class Builder<T> { public void Build() { } } } }";
        var nodes = await ParseAsync(code);

        var comp = RoslynSemantics.BuildCompilation(new[] { (Path, code) });
        var tree = comp.SyntaxTrees.Single();
        var model = comp.GetSemanticModel(tree);

        var builderSyntax = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(c => c.Identifier.Text == "Builder");
        var buildSyntax = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();

        var builderId = RoslynSemantics.ToNodeId(model.GetDeclaredSymbol(builderSyntax));
        var buildId = RoslynSemantics.ToNodeId(model.GetDeclaredSymbol(buildSyntax));

        Assert.Equal($"{Path}::My.App.Outer+Builder`1", builderId);
        Assert.Contains(nodes, n => n.Id == builderId);
        Assert.Contains(nodes, n => n.Id == buildId);
    }

    [Fact]
    public async Task RecordPrimaryConstructor_YieldsNoId_AndExplicitCtorIdsAgree()
    {
        // The primary ctor has no ConstructorDeclarationSyntax, so the parser emits no node for it —
        // mapping it to an id would create a dangling CALLS edge. The explicit ctor must not gain a
        // @span suffix from the invisible primary sibling either (same arity here: R(int) vs R(string)).
        const string code = "namespace N { public record R(int A) { public R(string s) : this(0) { } } }";
        var nodes = await ParseAsync(code);

        var comp = RoslynSemantics.BuildCompilation(new[] { (Path, code) });
        var tree = comp.SyntaxTrees.Single();
        var model = comp.GetSemanticModel(tree);
        var recordSymbol = (INamedTypeSymbol)model.GetDeclaredSymbol(
            tree.GetRoot().DescendantNodes().OfType<RecordDeclarationSyntax>().Single())!;

        var primary = recordSymbol.Constructors.Single(c =>
            c.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is RecordDeclarationSyntax));
        var explicitCtor = recordSymbol.Constructors.Single(c =>
            c.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is ConstructorDeclarationSyntax));

        Assert.Null(RoslynSemantics.ToNodeId(primary));

        var explicitId = RoslynSemantics.ToNodeId(explicitCtor);
        Assert.Equal($"{Path}::N.R::Constructor#1", explicitId);
        Assert.Contains(nodes, n => n.Id == explicitId);
    }
}
