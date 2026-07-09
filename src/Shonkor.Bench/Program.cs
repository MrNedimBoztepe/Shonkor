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
var (fts, semantic, hybrid) = await RetrievalBenchmark.RunAsync(provider, allNodes, k, Console.Out,
    golden, setPath is null ? null : Path.GetFileName(setPath));
Console.WriteLine();

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
        report.AppendLine($"Same query embeddings, {rag.Queries} queries, **matched token budget** (the baseline takes as many top chunks from {rag.Chunks} line-window chunks as fit within Shonkor's per-query token count) — so this compares COVERAGE at equal cost. Both sides now measure coverage on the **delivered text** (TICKET-202): the baseline's chunks and Shonkor's budgeted capsule string — symmetric, not the pre-budget subgraph.");
        report.AppendLine();
        report.AppendLine("| Retriever | Avg tokens delivered | Coverage of the target symbol |");
        report.AppendLine("|-----------|---------------------:|------------------------------:|");
        report.AppendLine($"| chunked-RAG (no graph) | {rag.RagAvgTokens:N0} | {rag.RagCoverage:P1} |");
        report.AppendLine($"| Shonkor capsule | {rag.ShonkorAvgTokens:N0} | {rag.ShonkorCoverage:P1} |");
        report.AppendLine();
        report.AppendLine($"Seed survival (fraction of semantic seed nodes still present in the delivered capsule after budgeting): **{rag.ShonkorSeedSurvival:P1}**.");
        report.AppendLine();
        var covDelta = (rag.ShonkorCoverage - rag.RagCoverage) * 100;
        report.AppendLine(covDelta >= 0
            ? $"→ At ~equal tokens, Shonkor covers the target **+{covDelta:F1} pp** more often ({rag.ShonkorCoverage:P0} vs {rag.RagCoverage:P0}) — and delivers it as a structured capsule (call graph + signatures), not raw chunks."
            : $"→ At ~equal tokens, chunked-RAG covers **{-covDelta:F1} pp** more ({rag.RagCoverage:P0} vs {rag.ShonkorCoverage:P0}); Shonkor's edge here is structure (call graph + signatures), not raw recall.");
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
Console.WriteLine("Wrote bench/report.md and bench/metrics.json");

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

static string? ArgValue(string[] a, string name)
{
    var i = Array.IndexOf(a, name);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
}

static IEmbeddingService? CreateEmbeddingService()
{
    try
    {
        var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var logger = LoggerFactory.Create(_ => { }).CreateLogger<OllamaEmbeddingService>();
        return new OllamaEmbeddingService(new HttpClient(), config, logger);
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
    return new OllamaSemanticAnalyzer(new HttpClient(), config, logger);
}
