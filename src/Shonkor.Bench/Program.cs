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
//   shonkor-bench <db> --set <f>       score a curated golden set JSON (e.g. bench/golden/intent.json)
//                                      instead of the auto-bootstrapped self-retrieval set
//   shonkor-bench <db> --baseline <f>  compare retrieval Precision@k against baseline JSON, flag drops (exit 2)

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Shonkor.Bench;
using Shonkor.Core.Interfaces;
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

Console.WriteLine("[1/2] Token reduction (budgeted capsule vs naive dump of the same subgraph)");
var token = await TokenBenchmark.RunAsync(provider, synthesizer, Console.Out);
Console.WriteLine($"  -> {token.ReductionPct:F1}% reduction over {token.Queries} queries ({token.NaiveTokens:N0} -> {token.CapsuleTokens:N0} tokens)\n");

Console.WriteLine("[2/2] Retrieval precision");
var (fts, semantic) = await RetrievalBenchmark.RunAsync(provider, allNodes, k, Console.Out,
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
        report.AppendLine($"Same query embeddings, {rag.Queries} queries, **matched token budget** (the baseline takes as many top chunks from {rag.Chunks} line-window chunks as fit within Shonkor's per-query token count) — so this compares COVERAGE at equal cost:");
        report.AppendLine();
        report.AppendLine("| Retriever | Avg tokens delivered | Coverage of the target symbol |");
        report.AppendLine("|-----------|---------------------:|------------------------------:|");
        report.AppendLine($"| chunked-RAG (no graph) | {rag.RagAvgTokens:N0} | {rag.RagCoverage:P1} |");
        report.AppendLine($"| Shonkor capsule | {rag.ShonkorAvgTokens:N0} | {rag.ShonkorCoverage:P1} |");
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
        const double tol = 0.03; // 3 pp regression tolerance
        var regressed = false;
        if (doc.RootElement.TryGetProperty("retrieval", out var ret))
        {
            foreach (var (mode, cur) in metrics)
            {
                if (ret.TryGetProperty(mode, out var bas)
                    && bas.TryGetProperty("precisionAtK", out var bpk)
                    && cur.PrecisionAtK < bpk.GetDouble() - tol)
                {
                    Console.Error.WriteLine($"[REGRESSION] {mode} Precision@k {cur.PrecisionAtK:F3} < baseline {bpk.GetDouble():F3} - {tol}");
                    regressed = true;
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
