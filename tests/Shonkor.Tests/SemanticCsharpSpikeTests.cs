// Licensed to Shonkor under the MIT License.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Shonkor.Core.Services;

namespace Shonkor.Tests;

/// <summary>
/// Spike for the semantic C# core (A-minus): proves that a Roslyn compilation + SemanticModel resolves
/// references to the EXACT symbol (where today's name matching is ambiguous), that the resolved symbol
/// maps back to the parser's node id, and surfaces the known overload-id collision.
/// </summary>
public class SemanticCsharpSpikeTests
{
    private static (SyntaxTree Tree, SemanticModel Model) ModelFor(CSharpCompilation comp, string path)
    {
        var tree = comp.SyntaxTrees.First(t => t.FilePath == path);
        return (tree, comp.GetSemanticModel(tree));
    }

    [Fact]
    public void ToNodeId_TypeReference_ResolvesToTheCorrectFile_AcrossSameNamedTypes()
    {
        // Two types both named "Thing" in different namespaces + files. User references the imported one.
        var comp = RoslynSemantics.BuildCompilation(new[]
        {
            ("/repo/AThing.cs", "namespace A { public class Thing { } }"),
            ("/repo/BThing.cs", "namespace B { public class Thing { } }"),
            ("/repo/User.cs",   "using A; namespace U { public class User { public Thing Field; } }")
        });

        var (tree, model) = ModelFor(comp, "/repo/User.cs");
        var fieldType = tree.GetRoot().DescendantNodes().OfType<FieldDeclarationSyntax>().First().Declaration.Type;
        var symbol = model.GetSymbolInfo(fieldType).Symbol;

        // Semantic resolution picks A.Thing (AThing.cs) — a name matcher couldn't disambiguate the two.
        Assert.Equal("/repo/AThing.cs::Thing", RoslynSemantics.ToNodeId(symbol));
    }

    [Fact]
    public void ToNodeId_BaseType_ResolvesToInterfaceNode()
    {
        var comp = RoslynSemantics.BuildCompilation(new[]
        {
            ("/repo/IBar.cs", "namespace N { public interface IBar { } }"),
            ("/repo/C.cs",    "using N; namespace N2 { public class C : IBar { } }")
        });

        var (tree, model) = ModelFor(comp, "/repo/C.cs");
        var baseType = tree.GetRoot().DescendantNodes().OfType<BaseTypeSyntax>().First().Type;
        var symbol = model.GetSymbolInfo(baseType).Symbol;

        Assert.Equal("/repo/IBar.cs::IBar", RoslynSemantics.ToNodeId(symbol));
    }

    [Fact]
    public void ToNodeId_Invocation_ResolvesToCalleeMethodNode()
    {
        var comp = RoslynSemantics.BuildCompilation(new[]
        {
            ("/repo/S.cs", "namespace N { public class S { public void Run() { Helper(); } public void Helper() { } } }")
        });

        var (tree, model) = ModelFor(comp, "/repo/S.cs");
        var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().First();
        var symbol = model.GetSymbolInfo(invocation).Symbol;

        // The CALLS edge would point caller -> this node id (arity-discriminated: Helper has 0 params).
        Assert.Equal("/repo/S.cs::S::Helper#0", RoslynSemantics.ToNodeId(symbol));
    }

    [Fact]
    public void ToNodeId_SameArityOverloads_Collide_KnownLimitation()
    {
        // The id encodes arity, not parameter types, so SAME-arity overloads still share one id
        // (Add(int) and Add(string) both -> ::C::Add#1). This is the accepted v1 residual ambiguity.
        var comp = RoslynSemantics.BuildCompilation(new[]
        {
            ("/repo/C.cs", "namespace N { public class C { public void Add(int a) { } public void Add(string a) { } } }")
        });

        var (tree, model) = ModelFor(comp, "/repo/C.cs");
        var methods = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
        var s1 = model.GetDeclaredSymbol(methods[0]);
        var s2 = model.GetDeclaredSymbol(methods[1]);

        Assert.Equal(RoslynSemantics.ToNodeId(s1), RoslynSemantics.ToNodeId(s2)); // collide -> same id (the open question)
    }

    [Fact]
    public void ToNodeId_ExternalSymbol_ReturnsNull()
    {
        var comp = RoslynSemantics.BuildCompilation(new[]
        {
            ("/repo/C.cs", "namespace N { public class C { public string Name; } }")
        });

        var (tree, model) = ModelFor(comp, "/repo/C.cs");
        var stringType = tree.GetRoot().DescendantNodes().OfType<FieldDeclarationSyntax>().First().Declaration.Type;
        var symbol = model.GetSymbolInfo(stringType).Symbol; // System.String — metadata only

        Assert.Null(RoslynSemantics.ToNodeId(symbol)); // no node for external symbols
    }

    [Fact]
    public async Task ParserNodeIds_MatchTheSemanticMapping()
    {
        // The parser and the semantic mapper share CsharpNodeId, so they agree on ids by construction.
        const string path = "/repo/P.cs";
        const string code = "namespace N { public class C { public void M() { } } }";

        var (nodes, _) = await new RoslynAstParser().ParseAsync(path, code);

        var comp = RoslynSemantics.BuildCompilation(new[] { (path, code) });
        var (tree, model) = ModelFor(comp, path);
        var typeSym = model.GetDeclaredSymbol(tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().First());
        var methodSym = model.GetDeclaredSymbol(tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().First());

        Assert.Contains(nodes, n => n.Id == RoslynSemantics.ToNodeId(typeSym));
        Assert.Contains(nodes, n => n.Id == RoslynSemantics.ToNodeId(methodSym));
    }
}
