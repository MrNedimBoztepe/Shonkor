// Licensed to Shonkor under the MIT License.

using System.Diagnostics;
using System.Text.Json;
using Shonkor.Core.Models;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Storage;
using StreamJsonRpc;

namespace Shonkor.Bench;

/// <summary>
/// The <c>--lsp-diff</c> mode (#467): loads the solution into a headless language server, waits for
/// readiness, and diffs the graph's SemanticSymbol pairs from the seed files against the server's
/// answers. Orchestration only — the decisions live in <see cref="LspDiff"/>, the wire in
/// <see cref="LspClient"/>, the rendering in <see cref="LspDiffReport"/>.
/// </summary>
internal static class LspDiffRunner
{
    public sealed record Options(
        string LspCommand,
        string SolutionPath,
        IReadOnlyList<string>? SeedFiles,
        bool LoadOnly,
        TimeSpan ReadyTimeout,
        string OutputDir);

    private const string SeedRule =
        "Seeds: SemanticSymbol edges grouped by the SOURCE node's file; the ten files with the most distinct "
        + "(source, target, relationship) pairs; ties broken ordinally by path. Overridable with --seed-files a;b;c.";

    public static async Task<int> RunAsync(SqliteGraphStorageProvider provider, IReadOnlyList<GraphNode> allNodes, Options o, TextWriter console)
    {
        var solution = Path.GetFullPath(o.SolutionPath);
        if (!File.Exists(solution))
        {
            console.WriteLine($"[Error] Solution not found at '{solution}'.");
            return 1;
        }
        var rootDir = Path.GetDirectoryName(solution)!;

        var indexedRevision = await provider.GetIndexedRevisionAsync().ConfigureAwait(false);
        var headRevision = GitHead(rootDir);
        if (!o.LoadOnly && !string.Equals(indexedRevision, headRevision, StringComparison.Ordinal))
        {
            console.WriteLine($"[Error] Graph was indexed at '{indexedRevision ?? "?"}' but the solution is at '{headRevision ?? "?"}'. "
                              + "Re-index (shonkor index) so both sides describe the same code, then rerun.");
            return 1;
        }
        // HEAD alone is not enough: the server reads the working tree, the graph read the tree at index time.
        // A modified .cs file shifts every line below the edit and the line-containment mapping silently
        // mis-attributes — measured as spurious "no-node" gaps in the very file being edited.
        var dirty = GitDirtyCsFiles(rootDir);
        if (!o.LoadOnly && dirty.Count > 0)
        {
            console.WriteLine($"[Error] {dirty.Count} .cs file(s) differ from HEAD in the working tree ({string.Join(", ", dirty.Take(5))}{(dirty.Count > 5 ? ", …" : "")}). "
                              + "Commit or stash, re-index, then rerun — the graph and the server must see the same text.");
            return 1;
        }

        var nodesById = allNodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var nodesByFile = LspDiff.GroupByFile(allNodes, rootDir);
        var edges = await provider.GetAllEdgesAsync().ConfigureAwait(false);
        var selection = LspDiff.SelectSeeds(nodesById, edges, rootDir);

        if (!o.LoadOnly)
        {
            if (selection.UnspecifiedInScope > 0)
            {
                console.WriteLine($"[Error] {selection.UnspecifiedInScope:N0} edge(s) of the relations under test carry Reason=Unspecified — this graph predates #428 or was never fully rescanned. Refusing to diff against it.");
                return 1;
            }
            if (selection.SemanticEdges == 0)
            {
                console.WriteLine("[Error] No SemanticSymbol edges — the graph was not built with Indexing:SemanticCSharp=true.");
                return 1;
            }
        }

        var seedFiles = o.SeedFiles is { Count: > 0 }
            ? o.SeedFiles.Select(f => Path.GetFullPath(f, rootDir)).ToList()
            : selection.Seeds.Select(s => s.File).ToList();
        var pairs = o.LoadOnly ? [] : LspDiff.SelectPairs(edges, nodesById, seedFiles, rootDir);

        var result = new LspDiffResult
        {
            ServerCommand = o.LspCommand,
            Solution = solution,
            RootDir = rootDir,
            IndexedRevision = indexedRevision,
            HeadRevision = headRevision,
            LoadOnly = o.LoadOnly,
            SeedRule = SeedRule,
            Seeds = o.SeedFiles is { Count: > 0 }
                ? seedFiles.Select(f => new SeedFile(f, pairs.Count(p => nodesById.TryGetValue(p.SourceId, out var s) && FilePaths.AreEqual(LspDiff.FullPathOf(s, rootDir), f)))).ToList()
                : selection.Seeds.ToList(),
            SemanticEdges = selection.SemanticEdges,
            UnspecifiedEdges = selection.UnspecifiedEdges
        };

        if (selection.UnspecifiedEdges > 0)
        {
            var offenders = string.Join(", ", edges.Where(e => e.Reason == ProvenanceReason.Unspecified)
                .GroupBy(e => e.Relationship, StringComparer.Ordinal).Select(g => $"{g.Key} ×{g.Count()}"));
            result.Notes.Add($"{selection.UnspecifiedEdges} edge(s) outside the relations under test carry Reason=Unspecified ({offenders}) — a producer that has not declared its reason (#428 gap), not a C# linker edge.");
        }
        console.WriteLine($"LSP diff: {pairs.Count} pair(s) from {seedFiles.Count} seed file(s); server `{o.LspCommand}`");
        Directory.CreateDirectory(o.OutputDir);
        await using var log = new StreamWriter(Path.Combine(o.OutputDir, "lsp-diff.log"), append: false) { AutoFlush = true };

        var client = LspClient.Start(o.LspCommand, log);
        await using (client.ConfigureAwait(false))
        {
            using var overall = new CancellationTokenSource(o.ReadyTimeout + TimeSpan.FromMinutes(30));
            var ct = overall.Token;

            await client.InitializeAsync(rootDir, ct).ConfigureAwait(false);
            result.Initialize = client.InitializeResult;
            result.TInitSeconds = client.InitElapsed?.TotalSeconds;
            console.WriteLine($"  initialize: {result.TInitSeconds:F1}s");

            // Step 0 — the two providers the diff cannot do without. Missing = csharp-ls becomes primary.
            foreach (var prov in new[] { "callHierarchyProvider", "implementationProvider", "referencesProvider", "documentSymbolProvider" })
                if (!result.HasProvider(prov)) result.Notes.Add($"Step 0: `{prov}` absent from the initialize result (check dynamic registrations).");

            await client.OpenSolutionAsync(solution).ConfigureAwait(false);
            result.OpenMode = "solution/open";
            console.WriteLine($"  solution/open sent; waiting up to {o.ReadyTimeout.TotalMinutes:F0} min for workspace/projectInitializationComplete …");
            var ready = await client.WaitForReadyAsync(o.ReadyTimeout, ct).ConfigureAwait(false);

            if (!ready)
            {
                // Fallback 1: the server did not understand the solution file — open the projects directly.
                var projects = Directory.EnumerateFiles(rootDir, "*.csproj", SearchOption.AllDirectories)
                    .Where(p => !LspDiff.IsGenerated(p) && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", FilePaths.Comparison))
                    .ToList();
                result.Notes.Add($"projectInitializationComplete did not arrive within {o.ReadyTimeout.TotalSeconds:F0}s after solution/open; fell back to project/open with {projects.Count} project(s).");
                await client.OpenProjectsAsync(projects).ConfigureAwait(false);
                result.OpenMode = "project/open (fallback)";
                ready = await client.WaitForReadyAsync(o.ReadyTimeout, ct).ConfigureAwait(false);
            }

            if (!ready)
            {
                // Fallback 2 (AC): first non-empty `references` on a control symbol marks readiness.
                ready = await ProbeReadyAsync(client, nodesByFile, rootDir, o.ReadyTimeout, ct, log).ConfigureAwait(false);
                if (ready) { client.MarkReadyByFallback(); result.ReadyByFallback = true; }
            }

            result.DynamicRegistrations = client.DynamicRegistrations.ToList();
            result.TReadySeconds = client.ReadyElapsed?.TotalSeconds;
            if (!ready)
            {
                result.Notes.Add("Server never became ready — no request was issued, no verdict exists.");
                console.WriteLine("  [EXPECTED ERROR] server never became ready; writing what was measured.");
                RecordLoadErrors(client, result);
                Write(result, o.OutputDir, console);
                return 1;
            }
            console.WriteLine($"  ready: {result.TReadySeconds:F1}s{(result.ReadyByFallback ? " (fallback probe)" : "")}");

            if (!o.LoadOnly)
            {
                // The reverse gap is measured against EVERY graph source of a target, not only the seed files'
                // — otherwise every non-seed caller the graph knows perfectly well would be counted as missing.
                var graphSources = edges
                    .Where(e => e.Reason == ProvenanceReason.SemanticSymbol)
                    .GroupBy(e => (e.TargetId, e.Relationship))
                    .ToDictionary(g => g.Key, g => (IReadOnlySet<string>)g.Select(e => e.SourceId).ToHashSet(StringComparer.Ordinal));
                await DiffAsync(client, pairs, graphSources, nodesById, nodesByFile, rootDir, result, console, log, ct).ConfigureAwait(false);
            }
            result.Timings = client.Timings.ToList();
            RecordLoadErrors(client, result);
        }

        Write(result, o.OutputDir, console);
        return 0;
    }

    private static async Task DiffAsync(LspClient client, IReadOnlyList<EdgePair> pairs,
        IReadOnlyDictionary<(string TargetId, string Relationship), IReadOnlySet<string>> allGraphSources, Dictionary<string, GraphNode> nodesById,
        Dictionary<string, List<GraphNode>> nodesByFile, string rootDir, LspDiffResult result, TextWriter console, TextWriter log, CancellationToken ct)
    {
        var symbolCache = new Dictionary<string, IReadOnlyList<DocumentSymbol>>(FilePaths.Comparer);
        var textCache = new Dictionary<string, string>(FilePaths.Comparer);
        var groups = pairs.GroupBy(p => (p.TargetId, p.Relationship)).ToList();
        var done = 0;

        foreach (var group in groups)
        {
            var (targetId, rel) = group.Key;
            result.TargetsQueried++;
            if (++done % 25 == 0) console.WriteLine($"  … {done}/{groups.Count} targets");

            if (!nodesById.TryGetValue(targetId, out var target) || LspDiff.FullPathOf(target, rootDir) is not { } targetFile)
            {
                foreach (var p in group) result.Pairs.Add(new PairResult(p, LspOutcome.Unmappable, "dangling target"));
                continue;
            }

            IReadOnlyList<LspLocation> locations;
            AnchorResult anchor;
            try
            {
                if (!symbolCache.TryGetValue(targetFile, out var symbols))
                    symbolCache[targetFile] = symbols = await client.DocumentSymbolsAsync(targetFile, ct).ConfigureAwait(false);
                anchor = LspDiff.FindAnchor(target, symbols);
                if (!anchor.Found)
                {
                    log.WriteLine($"[anchor] {anchor.Failure}: {target.Type} {target.Name} {target.StartLine}-{target.EndLine} in {targetFile}; symbols: "
                                  + string.Join(", ", symbols.Select(s => $"{s.Name}@{s.SelectionRange.Start.Line + 1}[{s.Range.Start.Line + 1}-{s.Range.End.Line + 1}]")));
                    foreach (var p in group) result.Pairs.Add(new PairResult(p, LspOutcome.Unmappable, $"anchor: {anchor.Failure}"));
                    continue;
                }
                result.TargetsAnchored++;

                locations = rel switch
                {
                    LspDiff.Calls => await client.IncomingCallersAsync(targetFile, anchor.Position!, ct).ConfigureAwait(false),
                    LspDiff.ReferencesType or LspDiff.Instantiates => await client.ReferencesAsync(targetFile, anchor.Position!, ct).ConfigureAwait(false),
                    LspDiff.Implements or LspDiff.Extends or LspDiff.Overrides or LspDiff.ImplementsMember
                        => await client.ImplementationsAsync(targetFile, anchor.Position!, ct).ConfigureAwait(false),
                    _ => []
                };
            }
            catch (RemoteInvocationException ex)
            {
                foreach (var p in group) result.Pairs.Add(new PairResult(p, LspOutcome.Unmappable, $"lsp error: {ex.Message}"));
                continue;
            }

            var mapped = locations.Select(l => (Location: l, Mapped: LspDiff.MapToNode(l.Uri, l.Range.Start.Line, rel, nodesByFile))).ToList();
            var lspIds = mapped.Where(m => m.Mapped.IsOk).Select(m => m.Mapped.NodeId!).ToHashSet(StringComparer.Ordinal);
            var graphSources = allGraphSources.GetValueOrDefault((targetId, rel)) ?? group.Select(p => p.SourceId).ToHashSet(StringComparer.Ordinal);

            foreach (var p in group)
            {
                var source = nodesById.GetValueOrDefault(p.SourceId);
                var outcome = LspDiff.Classify(p, anchorFound: true, source, lspIds);
                var note = outcome switch
                {
                    LspOutcome.Unmappable => "dangling source",
                    LspOutcome.Contradicted => $"server returned {locations.Count} location(s), {lspIds.Count} mapped"
                        + (rel == LspDiff.ReferencesType && !MentionsName(source!, target.Name, rootDir, textCache) ? "; source file has no textual occurrence of the target name (inferred `var`/lambda type — a reference the linker counts and the server does not)" : ""),
                    _ => null
                };
                result.Pairs.Add(new PairResult(p, outcome, note));
            }

            if (rel == LspDiff.Instantiates) continue;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (loc, m) in mapped)
            {
                if (m.IsOk && graphSources.Contains(m.NodeId!)) continue;
                var key = m.NodeId ?? $"{loc.Uri}:{loc.Range.Start.Line}";
                if (!seen.Add(key)) continue;
                var implicitSite = rel == LspDiff.Calls && m.IsOk && loc.Sites.Count > 0
                    && LspDiff.IsImplicitCall(LinesOf(LspDiff.FileOf(loc.Uri), textCache), loc.Sites, target.Name);
                result.Gaps.Add(new GapEntry(targetId, rel, loc.Uri, loc.Range.Start.Line, LspDiff.Bucket(loc.Uri, rootDir, m, implicitSite), m.NodeId, m.Status));
            }
        }
    }

    /// <summary>
    /// Readiness fallback: every 15 s, `documentSymbol` on a control file and `references` on its first type;
    /// the first non-empty answer is t_ready. Marked as a fallback in the report — it is a weaker signal than
    /// the server's own notification.
    /// </summary>
    private static async Task<bool> ProbeReadyAsync(LspClient client, Dictionary<string, List<GraphNode>> nodesByFile, string rootDir,
        TimeSpan budget, CancellationToken ct, TextWriter log)
    {
        var control = nodesByFile.Keys.FirstOrDefault(f => Path.GetFileName(f) == "SqliteGraphStorageProvider.cs" && FilePaths.TryGetRelative(f, rootDir, out _))
                      ?? Directory.EnumerateFiles(rootDir, "*.cs", SearchOption.AllDirectories).FirstOrDefault(f => !LspDiff.IsGenerated(f));
        if (control is null) return false;

        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                var symbols = await client.DocumentSymbolsAsync(control, ct).ConfigureAwait(false);
                var type = symbols.FirstOrDefault(s => s.Kind is 5 or 11 or 23); // Class, Interface, Struct
                if (type is not null)
                {
                    var refs = await client.ReferencesAsync(control, type.SelectionRange.Start, ct).ConfigureAwait(false);
                    if (refs.Count > 0) return true;
                }
            }
            catch (RemoteInvocationException ex)
            {
                log.WriteLine($"[probe] {ex.Message}");
            }
            await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        }
        return false;
    }

    private static void Write(LspDiffResult result, string outputDir, TextWriter console)
    {
        var md = Path.Combine(outputDir, "lsp-diff.md");
        var json = Path.Combine(outputDir, "lsp-diff.json");
        File.WriteAllText(md, LspDiffReport.Markdown(result));
        File.WriteAllText(json, LspDiffReport.Json(result));

        console.WriteLine();
        console.WriteLine($"t_init {result.TInitSeconds:F1}s, t_ready {result.TReadySeconds:F1}s{(result.ReadyByFallback ? " (fallback)" : "")}");
        if (result.ProjectLoadErrors.Count > 0)
            console.WriteLine($"  NOTE: {result.ProjectLoadErrors.Count} project load error(s) — results are not trustworthy (see {md} → Notes, and lsp-diff.log).");
        foreach (var rel in LspDiff.Relations)
        {
            var xs = result.Pairs.Where(p => p.Pair.Relationship == rel).ToList();
            if (xs.Count == 0) continue;
            console.WriteLine($"  {rel,-18} pairs {xs.Count,5}  confirmed {xs.Count(p => p.Outcome == LspOutcome.Confirmed),5}  contradicted {xs.Count(p => p.Outcome == LspOutcome.Contradicted),5}  unmappable {xs.Count(p => p.Outcome == LspOutcome.Unmappable),5}");
        }
        if (result.Gaps.Count > 0)
            console.WriteLine($"  reverse gap: {result.Gaps.Count} location(s) — other {result.Gaps.Count(g => g.Bucket == GapBucket.Other)}, linker-scope {result.Gaps.Count(g => g.Bucket == GapBucket.LinkerScope)}, unmappable {result.Gaps.Count(g => g.Bucket == GapBucket.Unmappable)}, generated {result.Gaps.Count(g => g.Bucket == GapBucket.Generated)}, external {result.Gaps.Count(g => g.Bucket == GapBucket.External)}");
        console.WriteLine($"Wrote {md} and {json}");
    }

    private static IReadOnlyList<string> LinesOf(string file, Dictionary<string, string> textCache)
    {
        if (!File.Exists(file)) return [];
        if (!textCache.TryGetValue(file, out var text)) textCache[file] = text = File.ReadAllText(file);
        return text.Split('\n');
    }

    /// <summary>Whether the source node's file mentions the target's name as an identifier — cheap evidence for "the graph saw an inferred type, the server saw no identifier".</summary>
    private static bool MentionsName(GraphNode source, string name, string rootDir, Dictionary<string, string> textCache)
    {
        var file = LspDiff.FullPathOf(source, rootDir);
        if (file is null || !File.Exists(file)) return true; // unknown — do not claim anything
        if (!textCache.TryGetValue(file, out var text)) textCache[file] = text = File.ReadAllText(file);
        return LspDiff.MentionsIdentifier(text, name);
    }

    /// <summary>Copies the server's project-load errors into the result and adds the "not trustworthy" note. Called once per run, right before the report is written.</summary>
    private static void RecordLoadErrors(LspClient client, LspDiffResult result)
    {
        result.ProjectLoadErrors = client.LoadErrors.ToList();
        if (LspDiff.ProjectLoadErrorNote(result.ProjectLoadErrors) is { } note) result.Notes.Add(note);
    }

    private static string? GitHead(string dir) => Git(dir, "rev-parse HEAD")?.Trim() is { Length: > 0 } head ? head : null;

    /// <summary>Tracked-modified and untracked <c>.cs</c> files — the ones whose text the server and the graph would disagree on.</summary>
    private static List<string> GitDirtyCsFiles(string dir) =>
        (Git(dir, "status --porcelain --untracked-files=all") ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Length > 3 ? l[3..].Trim() : string.Empty)
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

    private static string? Git(string dir, string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", $"-C \"{dir}\" {arguments}")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            });
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
