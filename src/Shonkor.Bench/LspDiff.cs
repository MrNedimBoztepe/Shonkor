// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Models;
using Shonkor.Core.Services;

namespace Shonkor.Bench;

/// <summary>Per-edge verdict of the LSP diff (#467). Never folded: three columns, always.</summary>
internal enum LspOutcome
{
    /// <summary>The language server named the graph's source among the answers for the target.</summary>
    Confirmed,

    /// <summary>The server answered, the answer mapped, and the graph's source was not among it.</summary>
    Contradicted,

    /// <summary>No anchor, a dangling node, or a source the mapping cannot decide — not a verdict about the edge.</summary>
    Unmappable
}

/// <summary>Why an LSP-only pair (the reverse gap) has no graph edge. The bucket that counts is <see cref="Other"/>.</summary>
internal enum GapBucket
{
    /// <summary>The location's file lies outside the solution root — a receiver the graph never indexed.</summary>
    External,

    /// <summary>Generated code (obj/, *.g.cs, *.Designer.cs, GlobalUsings, AssemblyInfo) — excluded by design.</summary>
    Generated,

    /// <summary>The file is in the graph but no node of the relation's granularity covers the line (lambda in a field initialiser, primary constructor, …) or two do.</summary>
    Unmappable,

    /// <summary>A node exists but the linker does not attribute that relation to its kind (e.g. CALLS from a constructor or property body — <c>SemanticCsharpLinker</c> only walks <c>MethodDeclarationSyntax</c>).</summary>
    LinkerScope,

    /// <summary>A node exists at the right granularity and the graph simply lacks the edge.</summary>
    Other
}

/// <summary>A deduplicated <c>(source, target, relationship)</c> — the graph's own pair granularity.</summary>
internal sealed record EdgePair(string SourceId, string TargetId, string Relationship);

/// <summary>A seed file with the number of distinct SemanticSymbol pairs originating in it.</summary>
internal sealed record SeedFile(string File, int Pairs);

/// <summary>The mechanical seed pick plus the sanity counters the report must show.</summary>
internal sealed record SeedSelection(IReadOnlyList<SeedFile> Seeds, int SemanticEdges, int UnspecifiedEdges);

/// <summary>Where to point the request for a target node, or why that is impossible.</summary>
internal sealed record AnchorResult(LspPosition? Position, string? Failure)
{
    public bool Found => Position is not null;
}

/// <summary>An LSP location mapped to a graph node at the relation's granularity — or the reason it was not.</summary>
/// <remarks><see cref="Status"/> is one of <c>ok</c>, <c>file-not-in-graph</c>, <c>no-node</c>, <c>ambiguous</c>, <c>linker-scope</c>.</remarks>
internal sealed record MappedSource(string? NodeId, string Status)
{
    public bool IsOk => Status == "ok";
    public static MappedSource Ok(string id) => new(id, "ok");
}

/// <summary>
/// The pure part of the LSP diff: seed selection, anchor search, location→node mapping, verdicts and gap
/// buckets. No I/O and no language server — everything here is testable with synthetic nodes and locations,
/// which is where the identity rules (line containment, never offsets; <see cref="FilePaths.Comparer"/>,
/// never a hand-picked comparison) are pinned.
/// </summary>
internal static class LspDiff
{
    public const string ReferencesType = "REFERENCES_TYPE";
    public const string Calls = "CALLS";
    public const string Instantiates = "INSTANTIATES";
    public const string Overrides = "OVERRIDES";
    public const string ImplementsMember = "IMPLEMENTS_MEMBER";

    /// <summary>The relations the semantic linker emits, in report order.</summary>
    public static readonly string[] Relations = [ReferencesType, Calls, Instantiates, Overrides, ImplementsMember];

    private static readonly HashSet<string> TypeKinds = new(StringComparer.Ordinal) { "Class", "Interface", "Record", "Struct", "Enum" };
    private static readonly HashSet<string> MemberKinds = new(StringComparer.Ordinal) { "Method", "Constructor", "Property" };

    /// <summary>A node's file as the absolute path the graph and the server will both be compared on.</summary>
    public static string? FullPathOf(GraphNode node, string rootDir) =>
        string.IsNullOrEmpty(node.FilePath) ? null : Path.GetFullPath(node.FilePath, rootDir);

    /// <summary>Nodes with a file and a line span, keyed by absolute path under the platform's path comparer.</summary>
    public static Dictionary<string, List<GraphNode>> GroupByFile(IEnumerable<GraphNode> nodes, string rootDir)
    {
        var byFile = new Dictionary<string, List<GraphNode>>(FilePaths.Comparer);
        foreach (var n in nodes)
        {
            if (n.StartLine is null || n.EndLine is null) continue;
            if (!TypeKinds.Contains(n.Type) && !MemberKinds.Contains(n.Type)) continue;
            var file = FullPathOf(n, rootDir);
            if (file is null) continue;
            if (!byFile.TryGetValue(file, out var list)) byFile[file] = list = [];
            list.Add(n);
        }
        return byFile;
    }

    /// <summary>
    /// The seed files, chosen mechanically: SemanticSymbol edges grouped by the source node's file, top
    /// <paramref name="top"/> by distinct <c>(source, target, relationship)</c> pairs, ties broken ordinally
    /// by path. Also counts <see cref="ProvenanceReason.Unspecified"/> edges — a graph with any is not a
    /// post-#428 graph and must not be diffed.
    /// </summary>
    public static SeedSelection SelectSeeds(IReadOnlyDictionary<string, GraphNode> nodesById, IEnumerable<GraphEdge> edges, string rootDir, int top = 10)
    {
        var pairsByFile = new Dictionary<string, HashSet<EdgePair>>(FilePaths.Comparer);
        var semantic = 0;
        var unspecified = 0;
        foreach (var e in edges)
        {
            if (e.Reason == ProvenanceReason.Unspecified) unspecified++;
            if (e.Reason != ProvenanceReason.SemanticSymbol) continue;
            semantic++;
            if (!nodesById.TryGetValue(e.SourceId, out var source)) continue;
            var file = FullPathOf(source, rootDir);
            if (file is null) continue;
            if (!pairsByFile.TryGetValue(file, out var set)) pairsByFile[file] = set = [];
            set.Add(new EdgePair(e.SourceId, e.TargetId, e.Relationship));
        }

        var seeds = pairsByFile
            .Select(kv => new SeedFile(kv.Key, kv.Value.Count))
            .OrderByDescending(s => s.Pairs)
            .ThenBy(s => s.File, StringComparer.Ordinal)
            .Take(top)
            .ToList();
        return new SeedSelection(seeds, semantic, unspecified);
    }

    /// <summary>All distinct SemanticSymbol pairs whose source node lives in one of <paramref name="seedFiles"/>.</summary>
    public static IReadOnlyList<EdgePair> SelectPairs(IEnumerable<GraphEdge> edges, IReadOnlyDictionary<string, GraphNode> nodesById, IEnumerable<string> seedFiles, string rootDir)
    {
        var seeds = new HashSet<string>(seedFiles, FilePaths.Comparer);
        var pairs = new HashSet<EdgePair>();
        foreach (var e in edges)
        {
            if (e.Reason != ProvenanceReason.SemanticSymbol) continue;
            if (!nodesById.TryGetValue(e.SourceId, out var source)) continue;
            var file = FullPathOf(source, rootDir);
            if (file is null || !seeds.Contains(file)) continue;
            pairs.Add(new EdgePair(e.SourceId, e.TargetId, e.Relationship));
        }
        return pairs.OrderBy(p => p.Relationship, StringComparer.Ordinal).ThenBy(p => p.TargetId, StringComparer.Ordinal).ThenBy(p => p.SourceId, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The request anchor for <paramref name="target"/>: the <c>selectionRange.start</c> of the document
    /// symbol with the node's name whose identifier line lies inside the node's span. Constructors carry
    /// the type's name on both sides (<c>RoslynAstParser</c> names the node after the identifier; Roslyn
    /// names the symbol <c>Foo(int)</c>), so no special case is needed beyond stripping the parameter list.
    /// Two candidates on the same line → unmappable, never a guess.
    /// </summary>
    public static AnchorResult FindAnchor(GraphNode target, IReadOnlyList<DocumentSymbol> symbols)
    {
        if (target.StartLine is not { } start || target.EndLine is not { } end) return new AnchorResult(null, "no line span");
        var wanted = BareName(target.Name);

        var loose = symbols.Where(s => BareName(s.Name) == wanted && Within(s.SelectionRange.Start.Line + 1, start, end)).ToList();
        if (loose.Count == 0) return new AnchorResult(null, "no symbol");

        var containing = loose.Where(s => s.Range.Start.Line + 1 <= start && end <= s.Range.End.Line + 1).ToList();
        var candidates = containing.Count > 0 ? containing : loose;
        if (candidates.Count > 1 && candidates.Select(c => c.SelectionRange.Start.Line).Distinct().Count() < candidates.Count)
            return new AnchorResult(null, "ambiguous line");

        var best = candidates.OrderBy(s => s.Range.End.Line - s.Range.Start.Line).First();
        return new AnchorResult(best.SelectionRange.Start, null);
    }

    /// <summary>
    /// The graph node an LSP location stands for, at the granularity the linker uses for
    /// <paramref name="relationship"/> — by line containment only. The file is compared through
    /// <see cref="FilePaths.Comparer"/> after <see cref="Path.GetFullPath(string)"/> of the URI's local path,
    /// so <c>file:///c:/x</c> and <c>C:\x</c> meet on Windows and stay apart on Linux.
    /// </summary>
    public static MappedSource MapToNode(string uri, int zeroBasedLine, string relationship, IReadOnlyDictionary<string, List<GraphNode>> nodesByFile)
    {
        var file = FileOf(uri);
        if (!nodesByFile.TryGetValue(file, out var nodes)) return new MappedSource(null, "file-not-in-graph");
        var line = zeroBasedLine + 1;
        var covering = nodes.Where(n => n.StartLine <= line && line <= n.EndLine).ToList();

        return relationship switch
        {
            // Linker: nearest enclosing TypeDeclarationSyntax (SemanticCsharpLinker.cs:253) — the innermost type.
            ReferencesType => Innermost(covering, TypeKinds) ?? new MappedSource(null, "no-node"),
            // Linker: nearest enclosing MethodDeclarationSyntax only (:280). A constructor/property body is a
            // real node the linker never attributes a CALLS to — that is linker scope, not a mapping failure.
            Calls => Innermost(covering, ["Method"]) ?? (Innermost(covering, MemberKinds) is { IsOk: true } member
                ? new MappedSource(member.NodeId, "linker-scope")
                : new MappedSource(null, "no-node")),
            // Linker: enclosing method, else enclosing type (:295-305).
            Instantiates => Innermost(covering, ["Method"]) ?? Innermost(covering, TypeKinds) ?? new MappedSource(null, "no-node"),
            Overrides or ImplementsMember => Innermost(covering, MemberKinds) ?? new MappedSource(null, "no-node"),
            _ => new MappedSource(null, "no-node")
        };
    }

    /// <summary>The verdict for one pair. Unmappable is not a verdict about the edge and is never counted as one.</summary>
    public static LspOutcome Classify(EdgePair pair, bool anchorFound, GraphNode? source, IReadOnlySet<string> lspSourceIds)
    {
        if (!anchorFound || source is null) return LspOutcome.Unmappable;
        return lspSourceIds.Contains(pair.SourceId) ? LspOutcome.Confirmed : LspOutcome.Contradicted;
    }

    /// <summary>Which cause an LSP-only location falls under. Generated is tested before external: <c>obj/</c> sits inside the root.</summary>
    public static GapBucket Bucket(string uri, string rootDir, MappedSource mapped)
    {
        var file = FileOf(uri);
        if (IsGenerated(file)) return GapBucket.Generated;
        if (!FilePaths.TryGetRelative(file, rootDir, out _)) return GapBucket.External;
        return mapped.Status switch
        {
            "ok" => GapBucket.Other,
            "linker-scope" => GapBucket.LinkerScope,
            _ => GapBucket.Unmappable
        };
    }

    /// <summary>The absolute local path behind a <c>file:</c> URI (or a plain path), normalised for comparison.</summary>
    public static string FileOf(string uriOrPath) =>
        Path.GetFullPath(Uri.TryCreate(uriOrPath, UriKind.Absolute, out var u) && u.IsFile ? u.LocalPath : uriOrPath);

    /// <summary>The identifier without parameter list, type arguments or explicit-interface prefix — what both sides call the symbol.</summary>
    public static string BareName(string name)
    {
        var cut = name.IndexOfAny(['(', '<']);
        var bare = cut >= 0 ? name[..cut] : name;
        var dot = bare.LastIndexOf('.');
        return (dot >= 0 ? bare[(dot + 1)..] : bare).Trim();
    }

    /// <summary>The percentile of an ascending list (nearest-rank), as <c>Program.cs</c> reports search latency.</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double p) =>
        sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, Math.Max(0, (int)Math.Ceiling(p / 100.0 * sorted.Count) - 1))];

    internal static bool IsGenerated(string file)
    {
        var name = Path.GetFileName(file);
        var sep = Path.DirectorySeparatorChar;
        return file.Contains($"{sep}obj{sep}", FilePaths.Comparison)
            || file.Contains("/obj/", FilePaths.Comparison)
            || name.EndsWith(".g.cs", FilePaths.Comparison)
            || name.EndsWith(".g.i.cs", FilePaths.Comparison)
            || name.EndsWith(".Designer.cs", FilePaths.Comparison)
            || name.StartsWith("GlobalUsings", FilePaths.Comparison)
            || name.Contains("AssemblyInfo", FilePaths.Comparison)
            || name.Contains("AssemblyAttributes", FilePaths.Comparison);
    }

    private static bool Within(int line, int start, int end) => start <= line && line <= end;

    /// <summary>The innermost node of the given kinds, or null when none; two with identical spans (one line, two members) → ambiguous.</summary>
    private static MappedSource? Innermost(List<GraphNode> covering, IReadOnlySet<string> kinds)
    {
        var ordered = covering.Where(n => kinds.Contains(n.Type)).OrderBy(n => n.EndLine - n.StartLine).ThenBy(n => n.StartLine).ToList();
        if (ordered.Count == 0) return null;
        if (ordered.Count > 1 && ordered[0].StartLine == ordered[1].StartLine && ordered[0].EndLine == ordered[1].EndLine)
            return new MappedSource(null, "ambiguous");
        return MappedSource.Ok(ordered[0].Id);
    }

    private static MappedSource? Innermost(List<GraphNode> covering, string[] kinds) =>
        Innermost(covering, new HashSet<string>(kinds, StringComparer.Ordinal));
}
