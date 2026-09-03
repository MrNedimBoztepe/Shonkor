// Licensed to Shonkor under the MIT License.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shonkor.Bench;

/// <summary>One pair's verdict, with the reason when it is not a verdict.</summary>
internal sealed record PairResult(EdgePair Pair, LspOutcome Outcome, string? Note);

/// <summary>An LSP-only answer (the reverse gap): where the server pointed, what it mapped to, and why the graph has no edge.</summary>
internal sealed record GapEntry(string TargetId, string Relationship, string Uri, int Line, GapBucket Bucket, string? NodeId, string Status);

/// <summary>Everything one <c>--lsp-diff</c> run measured. Serialised verbatim to <c>bench/lsp-diff.json</c>.</summary>
internal sealed class LspDiffResult
{
    public string ServerCommand { get; init; } = string.Empty;
    public string Solution { get; init; } = string.Empty;
    public string RootDir { get; init; } = string.Empty;
    public string? IndexedRevision { get; init; }
    public string? HeadRevision { get; init; }
    public bool LoadOnly { get; init; }
    public string SeedRule { get; init; } = string.Empty;
    public List<SeedFile> Seeds { get; init; } = [];
    public int SemanticEdges { get; init; }
    public int UnspecifiedEdges { get; init; }
    public JsonElement Initialize { get; set; }
    public List<string> DynamicRegistrations { get; set; } = [];
    public string OpenMode { get; set; } = string.Empty;
    public double? TInitSeconds { get; set; }
    public double? TReadySeconds { get; set; }
    public bool ReadyByFallback { get; set; }
    public List<LspTiming> Timings { get; set; } = [];
    public List<PairResult> Pairs { get; set; } = [];
    public List<GapEntry> Gaps { get; set; } = [];
    public int TargetsQueried { get; set; }
    public int TargetsAnchored { get; set; }
    public List<string> Notes { get; set; } = [];

    public bool HasProvider(string name) =>
        Initialize.ValueKind == JsonValueKind.Object
        && Initialize.TryGetProperty("capabilities", out var caps)
        && caps.TryGetProperty(name, out var p)
        && p.ValueKind is not (JsonValueKind.Null or JsonValueKind.False or JsonValueKind.Undefined);
}

/// <summary>Markdown + JSON rendering of an <see cref="LspDiffResult"/>. Raw output; the curated spike note is written by hand from it.</summary>
internal static class LspDiffReport
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Json(LspDiffResult r) => JsonSerializer.Serialize(r, JsonOptions);

    public static string Markdown(LspDiffResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# LSP diff (#467)");
        sb.AppendLine();
        sb.AppendLine($"- Server: `{r.ServerCommand}`");
        sb.AppendLine($"- Solution: `{r.Solution}`");
        sb.AppendLine($"- Graph revision: `{r.IndexedRevision ?? "?"}` — solution HEAD: `{r.HeadRevision ?? "?"}`");
        sb.AppendLine($"- Mode: {(r.LoadOnly ? "load-only (times, no diff)" : "diff")}");
        sb.AppendLine($"- Open: `{r.OpenMode}`");
        sb.AppendLine();

        sb.AppendLine("## Step 0 — initialize");
        sb.AppendLine();
        sb.AppendLine("| Provider | Static | Dynamic registration |");
        sb.AppendLine("|----------|:------:|:--------------------:|");
        foreach (var (prov, method) in new[]
                 {
                     ("documentSymbolProvider", "textDocument/documentSymbol"),
                     ("referencesProvider", "textDocument/references"),
                     ("callHierarchyProvider", "textDocument/prepareCallHierarchy"),
                     ("implementationProvider", "textDocument/implementation")
                 })
        {
            var dyn = r.DynamicRegistrations.Contains(method, StringComparer.Ordinal);
            sb.AppendLine($"| {prov} | {(r.HasProvider(prov) ? "yes" : "no")} | {(dyn ? "yes" : "no")} |");
        }
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine(r.Initialize.ValueKind == JsonValueKind.Undefined ? "null" : JsonSerializer.Serialize(r.Initialize, JsonOptions));
        sb.AppendLine("```");
        sb.AppendLine();

        sb.AppendLine("## Load time");
        sb.AppendLine();
        sb.AppendLine("| Mark | Seconds |");
        sb.AppendLine("|------|--------:|");
        sb.AppendLine($"| t_init (spawn → initialize) | {Fmt(r.TInitSeconds)} |");
        sb.AppendLine($"| t_ready (spawn → projectInitializationComplete{(r.ReadyByFallback ? ", FALLBACK: first non-empty references" : "")}) | {Fmt(r.TReadySeconds)} |");
        sb.AppendLine();

        if (r.Timings.Count > 0)
        {
            sb.AppendLine("## t_warm — per request after ready (ms)");
            sb.AppendLine();
            sb.AppendLine("| Method | n | median | p95 | max |");
            sb.AppendLine("|--------|--:|-------:|----:|----:|");
            foreach (var g in r.Timings.GroupBy(t => (t.Method, t.FirstForFile)).OrderBy(g => g.Key.Method, StringComparer.Ordinal).ThenBy(g => g.Key.FirstForFile))
            {
                var xs = g.Select(t => t.Milliseconds).Order().ToList();
                var label = g.Key.FirstForFile ? $"{g.Key.Method} (first per file)" : g.Key.Method;
                sb.AppendLine($"| {label} | {xs.Count} | {LspDiff.Percentile(xs, 50):F1} | {LspDiff.Percentile(xs, 95):F1} | {xs[^1]:F1} |");
            }
            sb.AppendLine();
        }

        if (r.LoadOnly)
        {
            AppendNotes(sb, r);
            return sb.ToString();
        }

        sb.AppendLine("## Seeds");
        sb.AppendLine();
        sb.AppendLine(r.SeedRule);
        sb.AppendLine();
        sb.AppendLine($"SemanticSymbol edges in graph: {r.SemanticEdges:N0}; Unspecified edges: {r.UnspecifiedEdges:N0}.");
        sb.AppendLine();
        sb.AppendLine("| Seed file | Distinct pairs |");
        sb.AppendLine("|-----------|---------------:|");
        foreach (var s in r.Seeds) sb.AppendLine($"| `{Rel(s.File, r.RootDir)}` | {s.Pairs} |");
        sb.AppendLine();

        sb.AppendLine("## Diff — graph pairs against the server");
        sb.AppendLine();
        sb.AppendLine($"Targets queried: {r.TargetsQueried}, anchored: {r.TargetsAnchored}.");
        sb.AppendLine();
        sb.AppendLine("| Relationship | Pairs | Confirmed | Contradicted | Unmappable |");
        sb.AppendLine("|--------------|------:|----------:|-------------:|-----------:|");
        foreach (var rel in LspDiff.Relations)
        {
            var xs = r.Pairs.Where(p => p.Pair.Relationship == rel).ToList();
            if (xs.Count == 0) continue;
            sb.AppendLine($"| {rel} | {xs.Count} | {xs.Count(p => p.Outcome == LspOutcome.Confirmed)} | {xs.Count(p => p.Outcome == LspOutcome.Contradicted)} | {xs.Count(p => p.Outcome == LspOutcome.Unmappable)} |");
        }
        sb.AppendLine();
        sb.AppendLine("INSTANTIATES is checked through `textDocument/references` on the constructed type: a confirmation there means "
                      + "\"the source references the type\", which is weaker than \"instantiates\". No reverse gap is computed for it.");
        sb.AppendLine();

        var unmappableReasons = r.Pairs.Where(p => p.Outcome == LspOutcome.Unmappable).GroupBy(p => p.Note ?? "?").OrderByDescending(g => g.Count()).ToList();
        if (unmappableReasons.Count > 0)
        {
            sb.AppendLine("### Unmappable — why");
            sb.AppendLine();
            sb.AppendLine("| Reason | Pairs |");
            sb.AppendLine("|--------|------:|");
            foreach (var g in unmappableReasons) sb.AppendLine($"| {g.Key} | {g.Count()} |");
            sb.AppendLine();
        }

        var contradicted = r.Pairs.Where(p => p.Outcome == LspOutcome.Contradicted).ToList();
        if (contradicted.Count > 0)
        {
            sb.AppendLine($"### Contradicted — {Math.Min(40, contradicted.Count)} of {contradicted.Count}");
            sb.AppendLine();
            foreach (var p in contradicted.Take(40)) sb.AppendLine($"- {p.Pair.Relationship}: `{p.Pair.SourceId}` → `{p.Pair.TargetId}`{(p.Note is null ? "" : $" — {p.Note}")}");
            sb.AppendLine();
        }

        sb.AppendLine("## Reverse gap — server pairs the graph lacks");
        sb.AppendLine();
        sb.AppendLine("| Relationship | External | Generated | Unmappable | LinkerScope | Other |");
        sb.AppendLine("|--------------|---------:|----------:|-----------:|------------:|------:|");
        foreach (var rel in LspDiff.Relations)
        {
            if (rel == LspDiff.Instantiates) continue;
            var xs = r.Gaps.Where(g => g.Relationship == rel).ToList();
            sb.AppendLine($"| {rel} | {xs.Count(g => g.Bucket == GapBucket.External)} | {xs.Count(g => g.Bucket == GapBucket.Generated)} | {xs.Count(g => g.Bucket == GapBucket.Unmappable)} | {xs.Count(g => g.Bucket == GapBucket.LinkerScope)} | {xs.Count(g => g.Bucket == GapBucket.Other)} |");
        }
        sb.AppendLine();
        sb.AppendLine("Buckets: External = file outside the solution root; Generated = obj/, *.g.cs, *.Designer.cs, GlobalUsings, AssemblyInfo; "
                      + "Unmappable = file in graph but no node of the relation's granularity at the line (or two); LinkerScope = a node exists but "
                      + "`SemanticCsharpLinker` never attributes that relation to its kind (CALLS from constructor/property bodies); "
                      + "**Other = node exists, edge missing — the number that counts.**");
        sb.AppendLine();
        foreach (var bucket in Enum.GetValues<GapBucket>())
        {
            var examples = r.Gaps.Where(g => g.Bucket == bucket).Take(bucket == GapBucket.Other ? 40 : 5).ToList();
            if (examples.Count == 0) continue;
            sb.AppendLine($"### {bucket} — examples");
            sb.AppendLine();
            foreach (var g in examples)
                sb.AppendLine($"- {g.Relationship} → `{g.TargetId}` from `{Rel(LspDiff.FileOf(g.Uri), r.RootDir)}:{g.Line + 1}`{(g.NodeId is null ? $" ({g.Status})" : $" = `{g.NodeId}`")}");
            sb.AppendLine();
        }

        AppendNotes(sb, r);
        return sb.ToString();
    }

    private static void AppendNotes(StringBuilder sb, LspDiffResult r)
    {
        if (r.Notes.Count == 0) return;
        sb.AppendLine("## Notes");
        sb.AppendLine();
        foreach (var n in r.Notes) sb.AppendLine($"- {n}");
        sb.AppendLine();
    }

    private static string Fmt(double? seconds) => seconds is { } s ? s.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : "—";

    private static string Rel(string file, string rootDir) =>
        Shonkor.Core.Services.FilePaths.TryGetRelative(file, rootDir, out var rel) ? rel.Replace('\\', '/') : file;
}
