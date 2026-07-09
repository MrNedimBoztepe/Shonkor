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
/// EXACT <c>IMPLEMENTS</c>/<c>EXTENDS</c>/<c>REFERENCES_TYPE</c>/<c>CALLS</c>/<c>OVERRIDES</c>/
/// <c>IMPLEMENTS_MEMBER</c>/<c>INSTANTIATES</c> edges, resolved to node ids via the <c>SemanticModel</c> —
/// where <see cref="CrossTechLinker"/>'s syntactic name matching is
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
    private const string Overrides = "OVERRIDES";
    private const string ImplementsMember = "IMPLEMENTS_MEMBER";
    private const string Instantiates = "INSTANTIATES";

    private static readonly string[] SemanticRelationships = { Implements, Extends, ReferencesType, Calls, Overrides, ImplementsMember, Instantiates };

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
        var edges = new HashSet<(string Source, string Target, string Relationship, Provenance Provenance)>();
        var unresolved = new HashSet<(string EnclosingId, string TypeName)>();
        await CollectEdgesAsync(compilation, trees, edges, unresolved, cancellationToken).ConfigureAwait(false);
        await ResolveUnresolvedByNameAsync(storage, unresolved, edges, cancellationToken).ConfigureAwait(false);

        if (edges.Count == 0) return;
        await storage.UpsertEdgesAsync(
            edges.Select(e => new GraphEdge { SourceId = e.Source, TargetId = e.Target, Relationship = e.Relationship, Provenance = e.Provenance }),
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
        var edges = new HashSet<(string Source, string Target, string Relationship, Provenance Provenance)>();
        var unresolved = new HashSet<(string EnclosingId, string TypeName)>();
        await CollectEdgesAsync(compilation, compilation.SyntaxTrees, edges, unresolved, cancellationToken).ConfigureAwait(false);

        // Non-lossy fallback (TICKET-004): for type references the compilation could NOT resolve (partial or
        // non-compiling checkouts), fall back to name-based edges so semantic mode never produces FEWER
        // REFERENCES_TYPE edges than the default resolver — only more precise ones where the symbol resolved.
        await ResolveUnresolvedByNameAsync(storage, unresolved, edges, cancellationToken).ConfigureAwait(false);

        if (edges.Count == 0) return;
        await storage.UpsertEdgesAsync(
            edges.Select(e => new GraphEdge { SourceId = e.Source, TargetId = e.Target, Relationship = e.Relationship, Provenance = e.Provenance }),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Emits name-based <c>REFERENCES_TYPE</c> edges for the type references the semantic model could not
    /// resolve to a symbol. Resolves the simple names against the graph's definition nodes (a name may map
    /// to several same-named types — the same residual ambiguity the default resolver has, but now confined
    /// to just the unresolved references rather than all of them).
    /// </summary>
    private static async Task ResolveUnresolvedByNameAsync(
        IGraphStore storage,
        HashSet<(string EnclosingId, string TypeName)> unresolved,
        HashSet<(string Source, string Target, string Relationship, Provenance Provenance)> edges,
        CancellationToken cancellationToken)
    {
        if (unresolved.Count == 0) return;

        var names = unresolved.Select(u => u.TypeName).Distinct().ToList();
        var definitions = await storage.GetDefinitionsByNamesAsync(names, cancellationToken).ConfigureAwait(false);
        if (definitions.Count == 0) return;

        foreach (var (enclosingId, typeName) in unresolved)
        {
            if (!definitions.TryGetValue(typeName, out var defs)) continue;

            // Heuristic, name-based resolution — NEVER Extracted (TICKET-207). A single candidate is a
            // plausible-but-unproven Inferred edge; multiple same-named candidates are Ambiguous (the same
            // residual ambiguity CrossTechLinker tags this way). A later exact resolution can upgrade the
            // trust via the MIN-provenance edge upsert.
            var eligible = defs.Where(d => d.Id != enclosingId).ToList();
            var provenance = eligible.Count > 1 ? Provenance.Ambiguous : Provenance.Inferred;
            foreach (var def in eligible)
            {
                edges.Add((enclosingId, def.Id, ReferencesType, provenance));
            }
        }
    }

    /// <summary>The simple (unqualified) type name of a syntax, or <c>null</c> for forms we don't name-match
    /// (predefined keywords, arrays, tuples, <c>var</c>).</summary>
    private static string? ExtractSimpleTypeName(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax id when id.Identifier.Text != "var" => id.Identifier.Text,
        GenericNameSyntax g => g.Identifier.Text,
        QualifiedNameSyntax q => ExtractSimpleTypeName(q.Right),
        _ => null
    };

    /// <summary>
    /// Walks the given <paramref name="trees"/> (each resolved against the whole <paramref name="compilation"/>)
    /// and collects the semantic edges they originate. Shared by the full link and the scoped relink.
    /// </summary>
    private static async Task CollectEdgesAsync(
        CSharpCompilation compilation,
        IEnumerable<SyntaxTree> trees,
        HashSet<(string Source, string Target, string Relationship, Provenance Provenance)> edges,
        HashSet<(string EnclosingId, string TypeName)> unresolved,
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
                    edges.Add((typeId, baseId, baseSymbol.TypeKind == TypeKind.Interface ? Implements : Extends, Provenance.Extracted));
                }
            }

            // REFERENCES_TYPE — every type usage, from the enclosing type to the referenced type.
            foreach (var typeSyntax in root.DescendantNodes().OfType<TypeSyntax>())
            {
                if (model.GetSymbolInfo(typeSyntax).Symbol is INamedTypeSymbol referencedType)
                {
                    var referencedId = RoslynSemantics.ToNodeId(referencedType);
                    if (referencedId is null) continue;

                    var enclosingType = typeSyntax.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                    var enclosingId = enclosingType is null ? null : RoslynSemantics.ToNodeId(model.GetDeclaredSymbol(enclosingType));
                    if (enclosingId is null || enclosingId == referencedId) continue;
                    edges.Add((enclosingId, referencedId, ReferencesType, Provenance.Extracted));
                }
                else if (typeSyntax is IdentifierNameSyntax or GenericNameSyntax)
                {
                    // Unresolved (partial/non-compiling checkout) — record the simple name for the name-based
                    // fallback so the edge isn't silently lost. Only leaf identifier/generic names, so the
                    // 'A' in a qualified 'A.Thing' isn't mistaken for a type.
                    var simpleName = ExtractSimpleTypeName(typeSyntax);
                    if (simpleName is null) continue;

                    var enclosingType = typeSyntax.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                    var enclosingId = enclosingType is null ? null : RoslynSemantics.ToNodeId(model.GetDeclaredSymbol(enclosingType));
                    if (enclosingId is null) continue;
                    unresolved.Add((enclosingId, simpleName));
                }
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
                edges.Add((callerId, calleeId, Calls, Provenance.Extracted));
            }

            // INSTANTIATES — each object creation (`new Foo()`, incl. `new()`), from the enclosing method
            // (or, in a field initializer, the enclosing type) to the CONSTRUCTED TYPE. Distinct from
            // REFERENCES_TYPE: it is method-level and means "actually creates instances of", not just "mentions".
            foreach (var creation in root.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(creation).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor) continue;
                var typeId = RoslynSemantics.ToNodeId(ctor.ContainingType);
                if (typeId is null) continue;

                var enclosingMethodDecl = creation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                string? sourceId;
                if (enclosingMethodDecl is not null)
                {
                    sourceId = RoslynSemantics.ToNodeId(model.GetDeclaredSymbol(enclosingMethodDecl));
                }
                else
                {
                    // Field/property initializer (no enclosing method) — attribute to the enclosing type.
                    var enclosingTypeDecl = creation.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                    sourceId = enclosingTypeDecl is null ? null : RoslynSemantics.ToNodeId(model.GetDeclaredSymbol(enclosingTypeDecl));
                }
                if (sourceId is null || sourceId == typeId) continue;
                edges.Add((sourceId, typeId, Instantiates, Provenance.Extracted));
            }

            // OVERRIDES — an override member to the base member it overrides (method-level override chain,
            // beyond the type-level EXTENDS). External bases (e.g. object.ToString) have no node → skipped.
            foreach (var memberDecl in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
            {
                if (memberDecl is not (MethodDeclarationSyntax or PropertyDeclarationSyntax)) continue;
                if (model.GetDeclaredSymbol(memberDecl) is not { IsOverride: true } declared) continue;

                var overridden = declared switch
                {
                    IMethodSymbol m => (ISymbol?)m.OverriddenMethod,
                    IPropertySymbol p => p.OverriddenProperty,
                    _ => null
                };
                var memberId = RoslynSemantics.ToNodeId(declared);
                var overriddenId = RoslynSemantics.ToNodeId(overridden);
                if (memberId is null || overriddenId is null || memberId == overriddenId) continue;
                edges.Add((memberId, overriddenId, Overrides, Provenance.Extracted));
            }

            // IMPLEMENTS_MEMBER — the concrete member that satisfies an interface member (method-level
            // counterpart to the type-level IMPLEMENTS). Emitted only when the implementing member is declared
            // in THIS tree, so the scoped relink (which clears/re-emits per file) stays consistent for partials.
            foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(typeDecl) is not INamedTypeSymbol typeSymbol) continue;
                if (typeSymbol.TypeKind == TypeKind.Interface) continue;

                foreach (var iface in typeSymbol.AllInterfaces)
                {
                    foreach (var member in iface.GetMembers())
                    {
                        if (member.Kind is not (SymbolKind.Method or SymbolKind.Property)) continue;
                        var impl = typeSymbol.FindImplementationForInterfaceMember(member);
                        if (impl is null) continue;
                        if (!impl.DeclaringSyntaxReferences.Any(r => r.SyntaxTree.FilePath == tree.FilePath)) continue;

                        var implId = RoslynSemantics.ToNodeId(impl);
                        var memberId = RoslynSemantics.ToNodeId(member);
                        if (implId is null || memberId is null || implId == memberId) continue;
                        edges.Add((implId, memberId, ImplementsMember, Provenance.Extracted));
                    }
                }
            }
        }
    }
}
