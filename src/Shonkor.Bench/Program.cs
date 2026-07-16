// Licensed to Shonkor under the MIT License.
//
// Shonkor.Bench — a single, unified, reproducible benchmark harness (replaces the former
// Shonkor.Benchmarks + Shonkor.Eval). Over a built graph DB it runs two headline measurements:
//   1. Token reduction  — budgeted capsule vs naive full-dump of the SAME retrieved subgraph.
//   2. Retrieval precision — Precision@1/@k, Recall@k, MRR for FTS (+ semantic when Ollama is reachable).
// Writes bench/report.md (human) and bench/metrics.json (machine), and can gate on a stored baseline.
//
// Usage:
//   shonkor-bench <db>                 run all benchmarks against <db> (default ./shonkor.db)
//   shonkor-bench <db> --k 10          retrieval cut-off k (default 10)
//   shonkor-bench <db> --set <f>       score a curated golden set JSON (e.g. bench/golden/agent-queries.json)
//                                      instead of the auto-bootstrapped self-retrieval set
//   shonkor-bench <db> --baseline <f>  gate P@1/MRR/Recall@k against baseline JSON, >5% rel drop exits 2
//   shonkor-bench --diff <a> <b>       per-case rank-1 diff between two runs' bench/cases.json (#174):
//                                      which cases changed their top hit, and in which direction. Needs no
//                                      database. A LENS, not a gate — always exits 0; --baseline owns
//                                      pass/fail. Every run writes bench/cases.json for this.
//   shonkor-bench <db> --check-circularity <set> [--words N]
//                                      validate a golden set for circularity (query vs target embedding
//                                      document); exits 2 if any case shares > N content words (default 4)
//   shonkor-bench <db> --answers <f>   answer-groundedness eval (TICKET-201) over a fixed-context golden
//                                      set (e.g. bench/golden/answers.json) through the production RAG
//                                      prompt — needs a reachable Ollama. Writes bench/answers-report.md +
//                                      bench/answers-metrics.json; with --baseline <answers-metrics.json>,
//                                      a >5% relative drop in any headline metric exits 2.

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Shonkor.Bench;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;
using Shonkor.Infrastructure.Storage;

var dbPath = args.FirstOrDefault(a => !a.StartsWith("--"))
             ?? Environment.GetEnvironmentVariable("SHONKOR_BENCH_DB")
             ?? "shonkor.db";

if (!File.Exists(dbPath))
{
    Console.Error.WriteLine($"[Error] Database not found at '{dbPath}'. Build one first (e.g. `shonkor index .`).");
    return 1;
}

var k = ArgValue(args, "--k") is { } ks && int.TryParse(ks, out var kp) ? kp : 10;
var baselinePath = ArgValue(args, "--baseline");
var setPath = ArgValue(args, "--set");

// --diff <before.json> <after.json> (#174): which cases changed their rank-1 hit between two runs.
// Placed before the database is opened — comparing two recorded runs needs no graph, and requiring one
// would stop you diffing a run you no longer have the DB for, which is precisely when you want to.
if (ArgValue(args, "--diff") is { } beforePath)
{
    var afterPath = ArgAfter(args, "--diff", 2);
    if (afterPath is null)
    {
        Console.Error.WriteLine("[Error] --diff takes TWO files: --diff <before-cases.json> <after-cases.json>.");
        return 2;
    }
    foreach (var p in new[] { beforePath, afterPath })
    {
        if (File.Exists(p)) continue;
        Console.Error.WriteLine($"[Error] Cases file not found at '{p}'.");
        return 2;
    }

    var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    var beforeRuns = JsonSerializer.Deserialize<Dictionary<string, List<CaseOutcome>>>(File.ReadAllText(beforePath), opts) ?? [];
    var afterRuns = JsonSerializer.Deserialize<Dictionary<string, List<CaseOutcome>>>(File.ReadAllText(afterPath), opts) ?? [];

    Console.WriteLine($"# Per-case rank-1 diff\n\n`{beforePath}` → `{afterPath}`\n");
    var regressions = 0;
    foreach (var retriever in beforeRuns.Keys.Intersect(afterRuns.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
    {
        var flips = CaseDiff.Compare(beforeRuns[retriever], afterRuns[retriever]);
        regressions += flips.Count(f => f.Direction == "REGRESSED");
        Console.WriteLine(CaseDiff.Report(flips, retriever));
    }

    // Exit 0 either way: this is a LENS, not a gate. The baseline gate (--baseline) owns pass/fail; a flip
    // is information to read, and failing on one would make people stop running it.
    Console.WriteLine(regressions == 0
        ? "No case regressed its rank-1 hit."
        : $"{regressions} case(s) regressed — read them before calling the aggregate noise.");
    return 0;
}
var compareRag = args.Contains("--compare-rag");

List<GoldenCase>? golden = null;
if (setPath is not null)
{
    if (!File.Exists(setPath)) { Console.Error.WriteLine($"[Error] Golden set not found at '{setPath}'."); return 1; }
    golden = JsonSerializer.Deserialize<List<GoldenCase>>(File.ReadAllText(setPath),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
}

var provider = new SqliteGraphStorageProvider(dbPath);
await provider.InitializeAsync();
var synthesizer = new ContextCapsuleSynthesizer();
var stats = await provider.GetStatisticsAsync();
var allNodes = await provider.GetAllNodesAsync();

Console.WriteLine("=== Shonkor Bench ===");
Console.WriteLine($"DB: {Path.GetFullPath(dbPath)} — {stats.TotalNodes:N0} nodes, {stats.TotalEdges:N0} edges\n");

// --gen-golden <out>: emit a natural-language golden set from the codebase's own XML doc summaries and exit.
// A more credible NL→code set than symbol-name self-retrieval (real prose queries, name stripped out).
var genGoldenPath = ArgValue(args, "--gen-golden");
if (genGoldenPath is not null)
{
    var generated = GoldenSetGenerator.Generate(allNodes);
    var full = Path.GetFullPath(genGoldenPath);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    File.WriteAllText(full, JsonSerializer.Serialize(generated, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Generated {generated.Count} doc-derived NL golden case(s) -> {genGoldenPath}");
    return 0;
}

// --search-latency: measure FTS5/BM25 seed latency on the CURRENT graph (#160). The docs carried a "<5 ms"
// figure of unknown provenance, published before FTS also indexed Summary (TICKET-211) and before the graph
// roughly doubled. This makes the claim reproducible instead of hand-maintained: warm up, then time each
// query and report median / p95 — the tail is what an agent actually feels, so a mean would flatter us.
if (args.Contains("--search-latency"))
{
    var queries = golden is { Count: > 0 }
        ? golden.Select(g => g.Query).ToList()
        : allNodes.Where(n => n.Type is "Class" or "Interface" or "Method")
                  .Select(n => n.Name).Distinct(StringComparer.Ordinal).Take(200).ToList();

    if (queries.Count == 0) { Console.WriteLine("No queries to time."); return 1; }

    foreach (var q in queries.Take(10)) await provider.SearchAsync(q, 10, 0, null); // warm the connection/page cache

    var timings = new List<double>(queries.Count);
    foreach (var q in queries)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await provider.SearchAsync(q, 10, 0, null);
        sw.Stop();
        timings.Add(sw.Elapsed.TotalMilliseconds);
    }
    timings.Sort();

    double Pct(List<double> xs, double p) => xs[Math.Min(xs.Count - 1, (int)Math.Ceiling(p / 100.0 * xs.Count) - 1)];
    Console.WriteLine($"Graph: {stats.TotalNodes:N0} nodes, {stats.TotalEdges:N0} edges — DB {new FileInfo(dbPath).Length / 1024.0 / 1024.0:F1} MB\n");
    Console.WriteLine($"FTS5 (BM25) seed latency over {timings.Count} queries");
    Console.WriteLine($"  median : {Pct(timings, 50):F2} ms");
    Console.WriteLine($"  p95    : {Pct(timings, 95):F2} ms");
    Console.WriteLine($"  max    : {timings[^1]:F2} ms");

    // 2-hop subgraph traversal (the recursive CTE) — the other latency the docs published unmeasured.
    var hopTimings = new List<double>();
    foreach (var q in queries)
    {
        var seedHits = await provider.SearchAsync(q, 3, 0, null);
        var seedIds = seedHits.Select(h => h.Node.Id).ToList();
        if (seedIds.Count == 0) continue;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await provider.GetSubgraphAsync(seedIds, 2);
        sw.Stop();
        hopTimings.Add(sw.Elapsed.TotalMilliseconds);
    }
    if (hopTimings.Count > 0)
    {
        hopTimings.Sort();
        Console.WriteLine($"\n2-hop subgraph traversal (recursive CTE) over {hopTimings.Count} seeds sets");
        Console.WriteLine($"  median : {Pct(hopTimings, 50):F2} ms");
        Console.WriteLine($"  p95    : {Pct(hopTimings, 95):F2} ms");
        Console.WriteLine($"  max    : {hopTimings[^1]:F2} ms");
    }
    return 0;
}

// --provenance: audit the trust-tier distribution per relationship (TICKET-207). Prints RelationType ×
// Provenance counts and flags any heuristic family (RELATES_TO, IMPORTS, OVERRIDES_BLOCK, BINDS_TO,
// name-fallback) that wrongly claims Extracted. Exit 2 if any such offender exists.
if (args.Contains("--provenance"))
{
    var allEdges = await provider.GetAllEdgesAsync();
    var byRel = allEdges
        .GroupBy(e => e.Relationship, StringComparer.Ordinal)
        .OrderBy(g => g.Key, StringComparer.Ordinal);

    var heuristicOnly = new HashSet<string>(StringComparer.Ordinal) { "RELATES_TO", "IMPORTS", "OVERRIDES_BLOCK", "BINDS_TO" };
    Console.WriteLine("Relationship               | Extracted | Inferred | Ambiguous");
    Console.WriteLine("---------------------------|----------:|---------:|---------:");
    var offenders = 0;
    foreach (var g in byRel)
    {
        var ex = g.Count(e => e.Provenance == Shonkor.Core.Models.Provenance.Extracted);
        var inf = g.Count(e => e.Provenance == Shonkor.Core.Models.Provenance.Inferred);
        var amb = g.Count(e => e.Provenance == Shonkor.Core.Models.Provenance.Ambiguous);
        var flag = heuristicOnly.Contains(g.Key) && ex > 0 ? "  ⚠ heuristic claims Extracted" : "";
        if (heuristicOnly.Contains(g.Key) && ex > 0) offenders += ex;
        Console.WriteLine($"{g.Key,-27}| {ex,9} | {inf,8} | {amb,9}{flag}");
    }
    Console.WriteLine($"\nHeuristic-family edges wrongly Extracted: {offenders}");
    return offenders > 0 ? 2 : 0;
}

// --check-circularity <set>: validate a retrieval golden set for CIRCULARITY (TICKET-202). For each case,
// resolve its target node, rebuild the exact embedding document (EmbeddingTextBuilder), and flag the case
// when the query shares more than N content words with it — i.e. the vector hit would be trivial. Use this
// to gate a curated/paraphrased set before trusting its precision. Exits 2 if any case is circular.
var checkCircPath = ArgValue(args, "--check-circularity");
if (checkCircPath is not null)
{
    if (!File.Exists(checkCircPath)) { Console.Error.WriteLine($"[Error] Set not found at '{checkCircPath}'."); return 1; }
    var setCases = JsonSerializer.Deserialize<List<GoldenCase>>(File.ReadAllText(checkCircPath),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<GoldenCase>();
    var threshold = ArgValue(args, "--words") is { } ws && int.TryParse(ws, out var wp) ? wp : CircularityCheck.DefaultThreshold;
    var embedSource = new ConfigurationBuilder().AddEnvironmentVariables().Build()["Embedding:Source"] ?? "code";

    // Resolve an Expected entry the same way the retrieval scorer matches: exact node id, or a node whose
    // Name equals it, or a node whose id contains it — so name-labeled sets (agent-queries) resolve too.
    var byIdMap = allNodes.ToDictionary(n => n.Id);
    var byName = allNodes.Where(n => !string.IsNullOrEmpty(n.Name))
        .GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    GraphNode? Resolve(string e) =>
        byIdMap.GetValueOrDefault(e)
        ?? byName.GetValueOrDefault(e)
        ?? allNodes.FirstOrDefault(n => n.Id.Contains(e, StringComparison.OrdinalIgnoreCase));

    var circular = new List<(string Id, int Shared)>();
    var checkable = 0;
    foreach (var c in setCases)
    {
        var target = c.Expected.Select(Resolve).FirstOrDefault(n => n is not null);
        if (target is null) continue; // can't check a case whose target isn't in this DB
        checkable++;
        var document = EmbeddingTextBuilder.Build(target, target.Summary, embedSource);
        var shared = CircularityCheck.SharedContentWordCount(c.Query, document);
        if (shared > threshold) circular.Add((c.Id, shared));
    }

    Console.WriteLine($"Circularity check of '{Path.GetFileName(checkCircPath)}' (threshold >{threshold} shared content words):");
    Console.WriteLine($"  {checkable} checkable case(s), {circular.Count} circular.");
    foreach (var (id, shared) in circular.OrderByDescending(x => x.Shared).Take(20))
        Console.WriteLine($"    ⚠ {id}: shares {shared} content words with its target's embedding document");
    if (circular.Count > 20) Console.WriteLine($"    … and {circular.Count - 20} more.");
    return circular.Count > 0 ? 2 : 0;
}

// --answers <f>: answer-groundedness eval (exclusive mode — generation over a local LLM is slow, so it
// doesn't piggyback on every bench run). Fixed per-case context isolates answer faithfulness from
// retrieval; the production RAG prompt (temperature=0, fixed seed) makes two runs reproducible.
var answersPath = ArgValue(args, "--answers");
if (answersPath is not null)
{
    if (!File.Exists(answersPath)) { Console.Error.WriteLine($"[Error] Answers golden set not found at '{answersPath}'."); return 1; }
    var answerCases = JsonSerializer.Deserialize<List<AnswerCase>>(File.ReadAllText(answersPath),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<AnswerCase>();
    if (answerCases.Count == 0) { Console.Error.WriteLine("[Error] Answers golden set is empty."); return 1; }

    var analyzer = CreateSemanticAnalyzer();
    Console.WriteLine($"Answer groundedness: {answerCases.Count} case(s) ({answerCases.Count(c => c.IsAbstain)} abstention) through the production RAG prompt");

    AnswersResult answers;
    try
    {
        answers = await AnswersBenchmark.RunAsync(provider, analyzer, answerCases, Console.Out);
    }
    catch (HttpRequestException ex)
    {
        Console.Error.WriteLine($"[Error] Ollama unreachable — the groundedness eval needs a running backend: {ex.Message}");
        return 1;
    }

    var aReport = new StringBuilder();
    aReport.AppendLine("# Shonkor Answer-Groundedness Report");
    aReport.AppendLine();
    aReport.AppendLine($"- DB: `{Path.GetFileName(dbPath)}` — {stats.TotalNodes:N0} nodes");
    aReport.AppendLine($"- Set: `{Path.GetFileName(answersPath)}` — {answers.Cases} cases ({answers.Answerable} answerable, {answers.Abstain} abstention, {answers.Skipped} skipped)");
    aReport.AppendLine();
    aReport.AppendLine("| Metric | Value |");
    aReport.AppendLine("|--------|------:|");
    aReport.AppendLine($"| Citation validity (valid refs / cited refs) | {answers.CitationValidity:F3} |");
    aReport.AppendLine($"| Must-cite recall | {answers.MustCiteRecall:F3} |");
    aReport.AppendLine($"| Abstention recall | {answers.AbstentionRecall:F3} |");
    aReport.AppendLine($"| Abstention precision | {answers.AbstentionPrecision:F3} |");
    aReport.AppendLine($"| Uncited-paragraph rate (lower is better) | {answers.UncitedParagraphRate:F3} |");
    aReport.AppendLine($"| Content checks passed (mustContain/mustNotContain) | {answers.ContentCheckPassRate:F3} |");
    if (answers.Failures.Count > 0)
    {
        aReport.AppendLine();
        aReport.AppendLine("## Failures");
        aReport.AppendLine();
        foreach (var f in answers.Failures) aReport.AppendLine($"- {f}");
    }

    Console.WriteLine();
    Console.WriteLine(aReport.ToString());

    Directory.CreateDirectory("bench");
    File.WriteAllText(Path.Combine("bench", "answers-report.md"), aReport.ToString());
    File.WriteAllText(Path.Combine("bench", "answers-metrics.json"),
        JsonSerializer.Serialize(answers, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    Console.WriteLine("Wrote bench/answers-report.md and bench/answers-metrics.json");

    // Gate the four headline metrics against a stored answers-metrics.json: >5% RELATIVE drop → exit 2.
    if (baselinePath is not null && File.Exists(baselinePath))
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(baselinePath));
            var gated = new (string Name, double Current)[]
            {
                ("citationValidity", answers.CitationValidity),
                ("mustCiteRecall", answers.MustCiteRecall),
                ("abstentionRecall", answers.AbstentionRecall),
                ("abstentionPrecision", answers.AbstentionPrecision)
            };
            var regressed = false;
            foreach (var (name, current) in gated)
            {
                if (doc.RootElement.TryGetProperty(name, out var b) && current < b.GetDouble() * 0.95)
                {
                    Console.Error.WriteLine($"[REGRESSION] {name} {current:F3} < baseline {b.GetDouble():F3} × 0.95");
                    regressed = true;
                }
            }
            if (regressed) return 2;
        }
        catch (JsonException)
        {
            Console.Error.WriteLine($"[WARN] Baseline '{baselinePath}' is not readable as answers metrics JSON — skipping the gate.");
        }
    }
    return 0;
}

Console.WriteLine("[1/2] Token reduction (budgeted capsule vs naive dump of the same subgraph)");
var token = await TokenBenchmark.RunAsync(provider, synthesizer, Console.Out);
Console.WriteLine($"  -> {token.ReductionPct:F1}% reduction over {token.Queries} queries ({token.NaiveTokens:N0} -> {token.CapsuleTokens:N0} tokens)\n");

Console.WriteLine("[2/2] Retrieval precision");
// Per-case rank-1 records (#174). Always collected — the artifact is only useful if it exists for the run
// you later want to compare against, and by then it is too late to have remembered a flag.
var outcomes = new Dictionary<string, List<CaseOutcome>>(StringComparer.Ordinal);
var (fts, semantic, hybrid, contamination) = await RetrievalBenchmark.RunAsync(provider, allNodes, k, Console.Out,
    golden, setPath is null ? null : Path.GetFileName(setPath), outcomes);
Console.WriteLine();

// #136: eval-corpus hygiene is a LOUD gate, not just a silent filter. If an index-excluded meta file
// (bench/golden, tickets, review) surfaced in retrieval, the shonkor.json exclude has broken and the numbers
// are re-contaminating — fail with a non-zero exit and name the offenders, like the other bench gates.
if (contamination.Count > 0)
{
    Console.Error.WriteLine(
        $"[FAIL] Eval re-contamination: {contamination.Count} index-excluded meta file(s) appeared in retrieval " +
        "results. They must not be in the graph at all — re-check shonkor.json's ExcludePatterns and re-index. " +
        "The measurement-time filter kept these numbers correct, but the exclude that should prevent them has regressed:");
    foreach (var file in contamination) Console.Error.WriteLine($"  - {file}");
    return 2;
}

var report = new StringBuilder();
report.AppendLine("# Shonkor Bench Report");
report.AppendLine();
report.AppendLine($"- DB: `{Path.GetFileName(dbPath)}` — {stats.TotalNodes:N0} nodes, {stats.TotalEdges:N0} edges");
report.AppendLine($"- k = {k}");
report.AppendLine();
report.AppendLine("## Token reduction");
report.AppendLine();
report.AppendLine("Budgeted capsule vs a naive full-dump of the **same** retrieved subgraph (not a whole-repo strawman):");
report.AppendLine();
report.AppendLine($"- **{token.ReductionPct:F1}%** reduction over {token.Queries} queries ({token.NaiveTokens:N0} -> {token.CapsuleTokens:N0} tokens)");
report.AppendLine();
report.AppendLine("## Retrieval precision");
report.AppendLine();
report.AppendLine("| Mode | Cases | Precision@1 | Precision@k | Recall@k | MRR |");
report.AppendLine("|------|------:|------------:|------------:|---------:|----:|");
report.AppendLine(fts.Row("graph (FTS5)"));
report.AppendLine(semantic is not null
    ? semantic.Row("semantic (vector)")
    : "| semantic (vector) | — | — | — | — | — (embedding backend unreachable) |");
report.AppendLine(hybrid is not null
    ? hybrid.Row("hybrid (RRF)")
    : "| hybrid (RRF) | — | — | — | — | — (embedding backend unreachable) |");
report.AppendLine();

// --compare-rag: head-to-head vs a naive chunked-RAG baseline (same embedding retrieval; Shonkor adds the
// graph + capsule budget). Uses the loaded --set, or a doc-derived NL set by default.
RagBaselineBenchmark.ComparisonResult? rag = null;
if (compareRag)
{
    Console.WriteLine("[3/3] RAG baseline head-to-head (chunked-RAG vs Shonkor capsule)");
    var ragCases = golden ?? GoldenSetGenerator.Generate(allNodes);
    rag = await RagBaselineBenchmark.RunAsync(provider, allNodes, CreateEmbeddingService(), ragCases, synthesizer, Console.Out);
    Console.WriteLine();

    report.AppendLine("## RAG baseline head-to-head");
    report.AppendLine();
    if (rag is null)
    {
        report.AppendLine("_Skipped — embedding backend unreachable._");
    }
    else
    {
        report.AppendLine($"Same query embeddings, {rag.Queries} queries, **matched token budget** (the baseline takes as many top chunks from {rag.Chunks} line-window chunks as fit within Shonkor's per-query token count) — so this compares COVERAGE at equal cost. Coverage is measured on the **delivered text** (TICKET-202), symmetric on both sides.");
        report.AppendLine();
        report.AppendLine("A 2×2 (#166): **retrieval strategy** (vector-only vs. hybrid = BM25 + vector, RRF) × **graph** (no / yes). The baseline's hybrid arm gets *exactly Shonkor's retrieval, minus the graph* — so the graph's true contribution is the like-for-like **hybrid diagonal**, not the mixed comparison.");
        report.AppendLine();
        report.AppendLine("| | chunked-RAG (no graph) | Shonkor capsule (graph) |");
        report.AppendLine("|---|---:|---:|");
        report.AppendLine($"| **vector-only seeds** | {rag.RagCoverage:P1} | {rag.ShonkorCoverage:P1} |");
        report.AppendLine($"| **hybrid seeds** | {rag.RagHybridCoverage:P1} | **{rag.ShonkorHybridCoverage:P1}** |");
        report.AppendLine();
        report.AppendLine($"Avg tokens delivered: baseline {rag.RagAvgTokens:N0}, Shonkor {rag.ShonkorAvgTokens:N0}. Seed survival through the capsule budget: **{rag.ShonkorSeedSurvival:P1}**. Vector-only capsule misses where the target was never a seed: **{rag.SeedMissedTarget}** of {rag.Queries}.");
        report.AppendLine();
        // The HONEST verdict is the like-for-like diagonal: same retrieval on both sides, graph the only
        // difference. Reading the graph's contribution off the mixed (Shonkor-hybrid vs baseline-vector)
        // comparison would credit the graph with the hybrid-retrieval gain (#166).
        var graphDelta = (rag.ShonkorHybridCoverage - rag.RagHybridCoverage) * 100;
        var hybridGain = (rag.RagHybridCoverage - rag.RagCoverage) * 100;
        report.AppendLine($"**The graph's isolated contribution** (hybrid diagonal, same retrieval both sides): **{graphDelta:+0.0;-0.0;0.0} pp** ({rag.ShonkorHybridCoverage:P1} vs {rag.RagHybridCoverage:P1}).");
        report.AppendLine($"Moving the *baseline* from vector-only to hybrid gains **{hybridGain:+0.0;-0.0;0.0} pp** — but read that with the next line before concluding the keyword arm is worthless.");
        report.AppendLine();
        report.AppendLine($"> **Why the baseline's keyword arm barely moves it:** it returned any hit on only **{rag.RagKeywordFiredQueries} of {rag.Queries}** queries. A raw 40-line source chunk does not keyword-match plain-English intent (\"how are api tokens hashed\" finds no chunk containing all those words). Shonkor's *nodes* do — a node carries a **name** and an **AI summary** that read like intent — which is why its hybrid arm gains where the baseline's does not. So the graph diagonal above is not pure topology: part of the gap is that the graph's indexed unit is keyword-matchable and a source chunk is not. That is a real advantage of the representation, named rather than hidden.");
        report.AppendLine();
        report.AppendLine(graphDelta > 0.05
            ? "→ On coverage, the graph adds measurable value even at like-for-like retrieval."
            : graphDelta < -0.05
                ? "→ **On coverage, the graph does NOT help** — at like-for-like retrieval the raw chunks cover the target at least as often. Coverage is the wrong axis for the graph's value: the graph's advantage is the **edges** (`references`, `related_tests`, blast radius), which coverage cannot measure. This number should re-anchor the pitch, not be buried."
                : "→ On coverage, the graph is **neutral** at like-for-like retrieval. Its value is the edges, which coverage does not capture — not raw recall.");
    }
    report.AppendLine();
}

Console.WriteLine(report.ToString());

var jsonOpts = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
var metrics = new Dictionary<string, MetricSet> { ["graph"] = fts };
if (semantic is not null) metrics["semantic"] = semantic;
if (hybrid is not null) metrics["hybrid"] = hybrid;

Directory.CreateDirectory("bench");
File.WriteAllText(Path.Combine("bench", "report.md"), report.ToString());
File.WriteAllText(Path.Combine("bench", "metrics.json"), JsonSerializer.Serialize(new
{
    tokenReductionPct = token.ReductionPct,
    tokenQueries = token.Queries,
    retrieval = metrics,
    ragBaseline = rag
}, jsonOpts));
File.WriteAllText(Path.Combine("bench", "cases.json"), JsonSerializer.Serialize(outcomes, jsonOpts));
Console.WriteLine("Wrote bench/report.md, bench/metrics.json and bench/cases.json");

if (baselinePath is not null && File.Exists(baselinePath))
{
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(baselinePath));
        // Gate on P@1, MRR and Recall@k with a RELATIVE tolerance (TICKET-202): P@k is dropped — at
        // 1 relevant/k it maxes at 1/k, so a 0.03 absolute tolerance made it a no-op. A >5% relative
        // drop in any headline metric of any retriever fails the run.
        const double rel = 0.95;
        var regressed = false;
        if (doc.RootElement.TryGetProperty("retrieval", out var ret))
        {
            foreach (var (mode, cur) in metrics)
            {
                if (!ret.TryGetProperty(mode, out var bas)) continue;
                (string Name, double Current, string Prop)[] gated =
                {
                    ("Precision@1", cur.PrecisionAt1, "precisionAt1"),
                    ("MRR", cur.Mrr, "mrr"),
                    ("Recall@k", cur.RecallAtK, "recallAtK")
                };
                foreach (var (name, current, prop) in gated)
                {
                    if (bas.TryGetProperty(prop, out var b) && current < b.GetDouble() * rel)
                    {
                        Console.Error.WriteLine($"[REGRESSION] {mode} {name} {current:F3} < baseline {b.GetDouble():F3} × {rel}");
                        regressed = true;
                    }
                }
            }
        }
        return regressed ? 2 : 0;
    }
    catch (JsonException)
    {
        Console.Error.WriteLine($"[WARN] Baseline '{baselinePath}' is not readable as bench metrics JSON — skipping the gate.");
    }
}

return 0;

static string? ArgValue(string[] a, string name) => ArgAfter(a, name, 1);

/// <summary>The <paramref name="offset"/>-th argument after <paramref name="name"/>, or null if absent.</summary>
/// <remarks>
/// <c>ArgValue</c> is the offset-1 case; <c>--diff</c> (#174) needs the second one too. One helper rather
/// than two so the "is it there, and is there room?" check exists once.
/// </remarks>
static string? ArgAfter(string[] a, string name, int offset)
{
    var i = Array.IndexOf(a, name);
    return i >= 0 && i + offset < a.Length ? a[i + offset] : null;
}

static IEmbeddingService? CreateEmbeddingService()
{
    try
    {
        var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var logger = LoggerFactory.Create(_ => { }).CreateLogger<OllamaEmbeddingService>();
        return OllamaClientFactory.CreateEmbeddingService(config, logger);
    }
    catch
    {
        return null;
    }
}

static ISemanticAnalyzer CreateSemanticAnalyzer()
{
    // Same construction as the web host's typed client: SemanticAnalyzer:OllamaUrl/OllamaModel from the
    // environment (e.g. SemanticAnalyzer__OllamaModel), defaulting to localhost + qwen2.5-coder.
    var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
    var logger = LoggerFactory.Create(_ => { }).CreateLogger<OllamaSemanticAnalyzer>();
    return OllamaClientFactory.CreateSemanticAnalyzer(config, logger);
}
