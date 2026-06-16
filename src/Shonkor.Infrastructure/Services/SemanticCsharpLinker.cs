// Licensed to Shonkor under the MIT License.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Core.Services;

namespace Shonkor.Infrastructure.Services;

/// <summary>
/// Semantic post-scan linker for C#: builds a Roslyn compilation over the project's source and emits
/// EXACT <c>IMPLEMENTS</c>/<c>EXTENDS</c>/<c>REFERENCES_TYPE</c>/<c>CALLS</c> edges, resolved to node ids
/// via the <c>SemanticModel</c> — where <see cref="CrossTechLinker"/>'s syntactic name matching is
/// ambiguous (same-named types in different namespaces). Reuses <see cref="RoslynSemantics"/>; node ids
/// match the parser via <see cref="CsharpNodeId"/>. Part of the semantic C# core (A-minus) project.
/// </summary>
/// <remarks>
/// Whole-graph by nature (a compilation needs all the sources), so it runs as a post-scan pass like
/// <see cref="CrossTechLinker"/> — not on a single-file reindex. The drift-remediation project covers
/// incremental maintenance. External symbols (declared only in metadata) have no node and are skipped.
/// </remarks>
public static class SemanticCsharpLinker
{
    private const string Implements = "IMPLEMENTS";
    private const string Extends = "EXTENDS";
    private const string ReferencesType = "REFERENCES_TYPE";
    private const string Calls = "CALLS";

    /// <summary>
    /// Reads the <c>.cs</c> files under <paramref name="directoryPath"/> (skipping bin/obj), builds a
    /// compilation, and links the semantic edges into <paramref name="storage"/>.
    /// </summary>
    public static async Task EstablishSemanticEdgesAsync(IGraphStore storage, string directoryPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        var files = new List<(string Path, string Code)>();
        foreach (var path in Directory.EnumerateFiles(directoryPath, "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                files.Add((Path.GetFullPath(path), await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)));
            }
            catch
            {
                // Unreadable file — skip; the scan already logged file-level issues.
            }
        }
        if (files.Count == 0) return;

        var compilation = RoslynSemantics.BuildCompilation(files);
        await LinkAsync(storage, compilation, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Emits the semantic edges from an already-built compilation. Exposed for testing. The parser's nodes
    /// must already be in <paramref name="storage"/> (their ids are what these edges point to).
    /// </summary>
    public static async Task LinkAsync(IGraphStore storage, CSharpCompilation compilation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(compilation);

        // Dedup by (source, target, relationship).
        var edges = new HashSet<(string Source, string Target, string Relationship)>();

        foreach (var tree in compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var model = compilation.GetSemanticModel(tree);
            var root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);

            // IMPLEMENTS / EXTENDS — base types/interfaces resolved to node ids (not names).
            foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (typeDecl.BaseList is null) continue;
                var typeId = RoslynSemantics.ToNodeId(model.GetDeclaredSymbol(typeDecl));
                if (typeId is null) continue;

                foreach (var baseType in typeDecl.BaseList.Types)
                {
                    if (model.GetSymbolInfo(baseType.Type).Symbol is not INamedTypeSymbol baseSymbol) continue;
                    var baseId = RoslynSemantics.ToNodeId(baseSymbol);
                    if (baseId is null || baseId == typeId) continue;
                    edges.Add((typeId, baseId, baseSymbol.TypeKind == TypeKind.Interface ? Implements : Extends));
                }
            }

            // REFERENCES_TYPE — every type usage, from the enclosing type to the referenced type.
            foreach (var typeSyntax in root.DescendantNodes().OfType<TypeSyntax>())
            {
                if (model.GetSymbolInfo(typeSyntax).Symbol is not INamedTypeSymbol referencedType) continue;
                var referencedId = RoslynSemantics.ToNodeId(referencedType);
                if (referencedId is null) continue;

                var enclosingType = typeSyntax.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                var enclosingId = enclosingType is null ? null : RoslynSemantics.ToNodeId(model.GetDeclaredSymbol(enclosingType));
                if (enclosingId is null || enclosingId == referencedId) continue;
                edges.Add((enclosingId, referencedId, ReferencesType));
            }

            // CALLS — each invocation, from the enclosing method to the callee method.
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol callee) continue;
                var calleeId = RoslynSemantics.ToNodeId(callee);
                if (calleeId is null) continue;

                var enclosingMethod = invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                var callerId = enclosingMethod is null ? null : RoslynSemantics.ToNodeId(model.GetDeclaredSymbol(enclosingMethod));
                if (callerId is null || callerId == calleeId) continue;
                edges.Add((callerId, calleeId, Calls));
            }
        }

        if (edges.Count == 0) return;
        await storage.UpsertEdgesAsync(
            edges.Select(e => new GraphEdge { SourceId = e.Source, TargetId = e.Target, Relationship = e.Relationship }),
            cancellationToken).ConfigureAwait(false);
    }
}
