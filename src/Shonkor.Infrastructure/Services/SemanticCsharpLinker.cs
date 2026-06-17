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

    private static readonly string[] SemanticRelationships = { Implements, Extends, ReferencesType, Calls };

    /// <summary>
    /// Reads the <c>.cs</c> files under <paramref name="directoryPath"/> (skipping bin/obj), builds a
    /// compilation, and links the semantic edges into <paramref name="storage"/>.
    /// </summary>
    public static async Task EstablishSemanticEdgesAsync(IGraphStore storage, string directoryPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        var compilation = await BuildCompilationForDirectoryAsync(directoryPath, cancellationToken).ConfigureAwait(false);
        if (compilation is null) return;

        await LinkAsync(storage, compilation, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a Roslyn compilation over the <c>.cs</c> files under <paramref name="directoryPath"/>
    /// (skipping bin/obj), or <c>null</c> when there are none. Shared by the full pass and the incremental
    /// reconcile (drift): the compilation needs ALL sources to resolve symbols, but the caller can then
    /// re-emit edges for only a subset of files via <see cref="RelinkFilesAsync"/>.
    /// </summary>
    public static async Task<CSharpCompilation?> BuildCompilationForDirectoryAsync(string directoryPath, CancellationToken cancellationToken = default)
    {
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

        return files.Count == 0 ? null : RoslynSemantics.BuildCompilation(files);
    }

    /// <summary>
    /// Incremental (drift) semantic relink: re-emits the semantic edges that ORIGINATE from
    /// <paramref name="filePaths"/>, after clearing those files' existing outgoing semantic edges — so a
    /// reconcile can refresh just the changed files (and their referencers) instead of the whole graph.
    /// The <paramref name="compilation"/> still spans the whole project (needed for symbol resolution), but
    /// only the listed files' syntax trees are walked. One compilation is built per reconcile batch.
    /// </summary>
    public static async Task RelinkFilesAsync(
        IGraphStore storage,
        CSharpCompilation compilation,
        IReadOnlySet<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(filePaths);
        if (filePaths.Count == 0) return;

        // Clear the stale outgoing semantic edges of every file we're about to re-emit (idempotent), so a
        // rename/remove doesn't leave danglers and a re-emit doesn't duplicate.
        foreach (var file in filePaths)
        {
            foreach (var rel in SemanticRelationships)
            {
                await storage.DeleteOutgoingEdgesByFilePathAsync(file, rel, cancellationToken).ConfigureAwait(false);
            }
        }

        var trees = compilation.SyntaxTrees.Where(t => filePaths.Contains(t.FilePath));
        var edges = new HashSet<(string Source, string Target, string Relationship)>();
        await CollectEdgesAsync(compilation, trees, edges, cancellationToken).ConfigureAwait(false);

        if (edges.Count == 0) return;
        await storage.UpsertEdgesAsync(
            edges.Select(e => new GraphEdge { SourceId = e.Source, TargetId = e.Target, Relationship = e.Relationship }),
            cancellationToken).ConfigureAwait(false);
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
        await CollectEdgesAsync(compilation, compilation.SyntaxTrees, edges, cancellationToken).ConfigureAwait(false);

        if (edges.Count == 0) return;
        await storage.UpsertEdgesAsync(
            edges.Select(e => new GraphEdge { SourceId = e.Source, TargetId = e.Target, Relationship = e.Relationship }),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Walks the given <paramref name="trees"/> (each resolved against the whole <paramref name="compilation"/>)
    /// and collects the semantic edges they originate. Shared by the full link and the scoped relink.
    /// </summary>
    private static async Task CollectEdgesAsync(
        CSharpCompilation compilation,
        IEnumerable<SyntaxTree> trees,
        HashSet<(string Source, string Target, string Relationship)> edges,
        CancellationToken cancellationToken)
    {
        foreach (var tree in trees)
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
    }
}
