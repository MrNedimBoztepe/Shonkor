// Licensed to Shonkor under the MIT License.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Shonkor.Core.Services;

/// <summary>
/// Semantic-core foundation (spike): builds a <see cref="CSharpCompilation"/> over a set of source files
/// and maps a resolved Roslyn <see cref="ISymbol"/> back to a Shonkor graph node id. The future
/// <c>SemanticCsharpLinker</c> uses these primitives to emit exact <c>REFERENCES_TYPE</c>/<c>IMPLEMENTS</c>/
/// <c>EXTENDS</c>/<c>CALLS</c> edges (to node ids, not names). See docs/projects/semantic-csharp-core.md.
/// </summary>
public static class RoslynSemantics
{
    /// <summary>
    /// Builds a compilation over the given <c>(path, code)</c> files using the host's reference assemblies
    /// (R1: the trusted-platform-assemblies list — no build, no NuGet restore). Intra-codebase symbols
    /// resolve because their sources are in the syntax trees; symbols in un-referenced third-party
    /// assemblies stay unresolved (and have no node anyway). The <c>path</c> sets each tree's FilePath,
    /// which <see cref="ToNodeId"/> relies on.
    /// </summary>
    public static CSharpCompilation BuildCompilation(IEnumerable<(string Path, string Code)> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var trees = files.Select(f => CSharpSyntaxTree.ParseText(f.Code, path: f.Path));
        return BuildCompilationFromTrees(trees);
    }

    /// <summary>Builds a compilation from already-parsed syntax trees (used by the incremental compilation cache).</summary>
    public static CSharpCompilation BuildCompilationFromTrees(IEnumerable<SyntaxTree> trees)
    {
        ArgumentNullException.ThrowIfNull(trees);
        return CSharpCompilation.Create(
            "ShonkorSemantic",
            trees,
            ReferenceAssemblies(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    /// <summary>The host's reference assemblies (TPA list), used as compilation references without a build.</summary>
    public static IReadOnlyList<MetadataReference> ReferenceAssemblies()
    {
        var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty;
        return tpa
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(File.Exists)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();
    }

    /// <summary>
    /// Maps a resolved symbol to its Shonkor node id (matching <see cref="CsharpNodeId"/>), or <c>null</c>
    /// when the symbol is external (declared only in metadata, e.g. <c>System.String</c>) and therefore
    /// has no node. Uses the symbol's first declaring syntax reference for the file path.
    /// </summary>
    public static string? ToNodeId(ISymbol? symbol)
    {
        if (symbol is null) return null;

        // An extension method invoked with member syntax (`a.Foo()`) resolves to the REDUCED symbol, whose
        // Parameters exclude the `this` parameter. The syntactic parser, however, counted `this` in the
        // method's arity. Use the original (unreduced) method so the arity — and thus the node id — match.
        if (symbol is IMethodSymbol { ReducedFrom: { } original }) symbol = original;

        var file = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.FilePath;
        if (string.IsNullOrEmpty(file)) return null; // external / metadata-only symbol

        return symbol switch
        {
            INamedTypeSymbol type => CsharpNodeId.ForType(file, TypeChain(type)),
            // Primary constructors (records / primary-ctor classes) have no ConstructorDeclarationSyntax,
            // so the syntactic parser creates no node for them — mapping one to an id would emit a
            // dangling edge. Skip them; explicit constructors resolve normally.
            IMethodSymbol { MethodKind: MethodKind.Constructor } ctor when ctor.ContainingType is not null
                => IsExplicitConstructor(ctor)
                    ? CsharpNodeId.ForMethod(file, TypeChain(ctor.ContainingType), "Constructor", ctor.Parameters.Length, OverloadSpan(ctor))
                    : null,
            IMethodSymbol method when method.ContainingType is not null
                => CsharpNodeId.ForMethod(file, TypeChain(method.ContainingType), method.Name, method.Parameters.Length, OverloadSpan(method)),
            IPropertySymbol prop when prop.ContainingType is not null
                => CsharpNodeId.ForMember(file, TypeChain(prop.ContainingType), prop.Name),
            _ => null
        };
    }

    /// <summary>
    /// The symbol-side counterpart of the parser's <c>TypeChainOf</c>: the type's full chain
    /// (<c>{Namespace}.{Outer}+{Nested}</c>, generic arity via <see cref="ISymbol.MetadataName"/>'s
    /// backtick suffix). Both sides derive the chain from the same declaration structure, so ids match.
    /// </summary>
    private static string TypeChain(INamedTypeSymbol type)
    {
        var segments = new List<string>();
        for (var t = type; t is not null; t = t.ContainingType)
        {
            segments.Insert(0, t.MetadataName);
        }
        var chain = string.Join("+", segments);
        return type.ContainingNamespace is { IsGlobalNamespace: false } ns
            ? $"{ns.ToDisplayString()}.{chain}"
            : chain;
    }

    /// <summary>True when the constructor symbol is declared by an explicit <c>ConstructorDeclarationSyntax</c> (not a primary constructor).</summary>
    private static bool IsExplicitConstructor(IMethodSymbol ctor) =>
        ctor.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is Microsoft.CodeAnalysis.CSharp.Syntax.ConstructorDeclarationSyntax);

    /// <summary>
    /// Returns the declaration's source offset when <paramref name="method"/> has a same-kind, same-arity
    /// overload sibling in its containing type — mirroring the parser's <c>MethodOverloadSpan</c> so node
    /// and edge ids match. Returns <c>null</c> for non-overloaded methods (stable name#arity id).
    /// </summary>
    private static int? OverloadSpan(IMethodSymbol method)
    {
        // Primary constructors are invisible to the syntactic parser (no ConstructorDeclarationSyntax),
        // so they must not count as overload siblings either — otherwise the explicit ctor's id would
        // gain a @span suffix the parser never emits.
        var siblings = method.ContainingType
            .GetMembers(method.Name)
            .OfType<IMethodSymbol>()
            .Count(m => m.MethodKind == method.MethodKind
                        && m.Parameters.Length == method.Parameters.Length
                        && (m.MethodKind != MethodKind.Constructor || IsExplicitConstructor(m)));

        if (siblings <= 1) return null;

        var span = method.DeclaringSyntaxReferences.FirstOrDefault()?.Span;
        return span?.Start;
    }
}
