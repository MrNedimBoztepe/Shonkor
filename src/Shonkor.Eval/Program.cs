// Licensed to Shonkor under the MIT License.
//
// Shonkor.Eval — a lean, repeatable PRECISION evaluation harness (see review/eval-plan.md).
//
// Level 1 (deterministic, no LLM): retrieval precision over the graph — Precision@k, Recall@k, MRR
//   for FTS (`search_graph`) and, when an embedding backend is reachable, vector (`search_semantic`).
// The golden set is a list of {query, mode, expected node-id substrings}. When no set is supplied it
// auto-bootstraps a self-retrieval set from the DB (query a symbol by its own name, expect itself),
// which honestly measures FTS ranking/tokenisation quality without hand-curated fixtures.
//
// Usage:
//   shonkor-eval <db>            run the eval against <db> (default ./shonkor.db)
//   shonkor-eval <db> --dump     print DB stats + a sample of node names (to author a golden set)
//   shonkor-eval <db> --set <f>  use the golden set JSON at <f> instead of auto-bootstrapping
//   shonkor-eval <db> --k 10     cut-off k (default 10)
//   shonkor-eval <db> --baseline eval/baseline.json   compare against a stored baseline and flag drops

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Infrastructure.Services;
using Shonkor.Infrastructure.Storage;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var dbPath = args.FirstOrDefault(a => !a.StartsWith("--"))
                     ?? Environment.GetEnvironmentVariable("SHONKOR_EVAL_DB")
                     ?? "shonkor.db";

        if (!File.Exists(dbPath))
        {
            Console.Error.WriteLine($"[Error] Database not found at '{dbPath}'.");
            return 1;
        }

        int k = ArgValue(args, "--k") is { } ks && int.TryParse(ks, out var kp) ? kp : 10;
        var setPath = ArgValue(args, "--set");
        var baselinePath = ArgValue(args, "--baseline");
        var dump = args.Contains("--dump");

        var provider = new SqliteGraphStorageProvider(dbPath);
        await provider.InitializeAsync();

        var stats = await provider.GetStatisticsAsync();
        var allNodes = await provider.GetAllNodesAsync();
        var withSummary = allNodes.Count(n => !string.IsNullOrWhiteSpace(n.Summary));
        // GetAllNodesAsync/ReadNode does not load the Embedding BLOB (callers don't need it), so count it
        // directly against the DB file rather than off the materialized nodes.
        var withEmbedding = CountNodesWithEmbedding(dbPath);

        Console.WriteLine("=== Shonkor Precision Eval ===");
        Console.WriteLine($"DB: {Path.GetFullPath(dbPath)}");
        Console.WriteLine($"Nodes: {stats.TotalNodes:N0} | Edges: {stats.TotalEdges:N0}");
        Console.WriteLine($"Nodes with summary: {withSummary:N0} ({Pct(withSummary, allNodes.Count)}) | with embedding: {withEmbedding:N0} ({Pct(withEmbedding, allNodes.Count)})");
        Console.WriteLine($"Node types: {string.Join(", ", stats.NodesByType.OrderByDescending(t => t.Value).Take(12).Select(t => $"{t.Key}:{t.Value}"))}");
        Console.WriteLine();

        if (dump)
        {
            DumpSample(allNodes);
            return 0;
        }

        // Answer/grounding mode (TICKET-102): citation validity, must-cite, abstention over the RAG path.
        if (args.Contains("--answers"))
        {
            var answersPath = setPath ?? Path.Combine("eval", "golden", "answers.json");
            if (!File.Exists(answersPath))
            {
                Console.Error.WriteLine($"No answer golden set at '{answersPath}'.");
                return 1;
            }
            var answerCases = JsonSerializer.Deserialize<List<GoldenCase>>(File.ReadAllText(answersPath), JsonOpts) ?? new();
            var analyzer = TryCreateSemanticAnalyzer();
            if (analyzer is null)
            {
                Console.Error.WriteLine("No semantic analyzer backend.");
                return 1;
            }
            await AnswerEval.RunAsync(provider, analyzer, answerCases, contextK: ArgValue(args, "--context-k") is { } ck && int.TryParse(ck, out var ckp) ? ckp : 5);
            return 0;
        }

        // Experiment mode: real semantic/hybrid numbers via the live embedding backend.
        if (args.Contains("--experiment"))
        {
            var set = setPath is not null && File.Exists(setPath)
                ? JsonSerializer.Deserialize<List<GoldenCase>>(File.ReadAllText(setPath), JsonOpts) ?? new()
                : Bootstrap(allNodes);
            var emb = TryCreateEmbeddingService();
            if (emb is null) { Console.Error.WriteLine("No embedding backend."); return 1; }
            var source = ArgValue(args, "--embed-source") ?? "code";
            var usePrefix = !args.Contains("--no-prefix");
            await SemanticExperiment.RunAsync(provider, allNodes, emb, set, k, source, usePrefix);
            return 0;
        }

        // Build the golden set.
        List<GoldenCase> cases = setPath is not null && File.Exists(setPath)
            ? JsonSerializer.Deserialize<List<GoldenCase>>(File.ReadAllText(setPath), JsonOpts) ?? new()
            : Bootstrap(allNodes);

        // --force-mode lets one golden set be scored under a chosen retriever (graph|semantic) against the
        // same DB, so graph-vs-semantic is a clean apples-to-apples comparison on identical queries.
        if (ArgValue(args, "--force-mode") is { } forced)
        {
            foreach (var c in cases) c.Mode = forced;
        }

        Console.WriteLine($"Golden cases: {cases.Count} ({(setPath is null ? "auto-bootstrapped self-retrieval" : setPath)})");
        Console.WriteLine();

        // Optional embedding backend for semantic cases.
        IEmbeddingService? embedding = TryCreateEmbeddingService();
        bool semanticReachable = false;
        if (cases.Any(c => c.Mode == "semantic") && embedding is not null)
        {
            try { var probe = await embedding.GenerateEmbeddingAsync("probe"); semanticReachable = probe.Length > 0; }
            catch { semanticReachable = false; }
        }

        var ftsCases = cases.Where(c => c.Mode is null or "graph").ToList();
        var semCases = cases.Where(c => c.Mode == "semantic").ToList();

        var ftsMetrics = await RunRetrieval(provider, embedding, ftsCases, k, semantic: false, semanticReachable);
        var semMetrics = semCases.Count == 0
            ? null
            : (semanticReachable
                ? await RunRetrieval(provider, embedding, semCases, k, semantic: true, semanticReachable)
                : null);

        var report = new StringBuilder();
        report.AppendLine("# Shonkor Eval Report");
        report.AppendLine();
        report.AppendLine($"- DB: `{Path.GetFileName(dbPath)}` — {stats.TotalNodes:N0} nodes, {stats.TotalEdges:N0} edges");
        report.AppendLine($"- Summaries: {Pct(withSummary, allNodes.Count)} | Embeddings: {Pct(withEmbedding, allNodes.Count)}");
        report.AppendLine($"- k = {k} | cases = {cases.Count}");
        report.AppendLine();
        report.AppendLine("| Mode | Cases | Precision@1 | Precision@k | Recall@k | MRR |");
        report.AppendLine("|------|------:|------------:|------------:|---------:|----:|");
        report.AppendLine(ftsMetrics.Row("graph (FTS5)"));
        if (semMetrics is not null) report.AppendLine(semMetrics.Row("semantic (vector)"));
        else if (semCases.Count > 0) report.AppendLine($"| semantic (vector) | {semCases.Count} | — | — | — | — (embedding backend unreachable — skipped) |");
        report.AppendLine();

        Console.WriteLine(report.ToString());

        Directory.CreateDirectory("eval");
        File.WriteAllText(Path.Combine("eval", "last-report.md"), report.ToString());

        var current = new Dictionary<string, MetricSet> { ["graph"] = ftsMetrics };
        if (semMetrics is not null) current["semantic"] = semMetrics;
        File.WriteAllText(Path.Combine("eval", "last-metrics.json"), JsonSerializer.Serialize(current, JsonOpts));
        Console.WriteLine("Wrote eval/last-report.md and eval/last-metrics.json");

        if (baselinePath is not null && File.Exists(baselinePath))
        {
            Dictionary<string, MetricSet> baseline;
            try
            {
                baseline = JsonSerializer.Deserialize<Dictionary<string, MetricSet>>(File.ReadAllText(baselinePath), JsonOpts) ?? new();
            }
            catch (JsonException)
            {
                Console.Error.WriteLine($"[WARN] Baseline '{baselinePath}' is not in the machine metric shape (a map of mode -> {{precisionAt1, precisionAtK, recallAtK, mrr}}). Skipping the regression gate. Generate one by copying eval/last-metrics.json.");
                return 0;
            }
            const double tol = 0.03; // 3 pp regression tolerance
            bool regressed = false;
            foreach (var (mode, cur) in current)
            {
                if (baseline.TryGetValue(mode, out var bas) && cur.PrecisionAtK < bas.PrecisionAtK - tol)
                {
                    Console.Error.WriteLine($"[REGRESSION] {mode} Precision@k {cur.PrecisionAtK:F3} < baseline {bas.PrecisionAtK:F3} - {tol}");
                    regressed = true;
                }
            }
            return regressed ? 2 : 0;
        }

        return 0;
    }

    private static async Task<MetricSet> RunRetrieval(
        SqliteGraphStorageProvider provider, IEmbeddingService? embedding,
        List<GoldenCase> cases, int k, bool semantic, bool semanticReachable)
    {
        double sumP1 = 0, sumPk = 0, sumRecall = 0, sumRr = 0;
        int n = 0;

        foreach (var c in cases)
        {
            IReadOnlyList<SearchResult> hits;
            if (semantic)
            {
                if (!semanticReachable || embedding is null) continue;
                var qv = await embedding.GenerateEmbeddingAsync(c.Query);
                hits = await provider.SearchSemanticAsync(qv, k);
            }
            else
            {
                hits = await provider.SearchAsync(c.Query, k);
            }

            var ranked = hits.Select(h => h.Node).ToList();
            bool Match(GraphNode node) => c.Expected.Any(e =>
                node.Id.Contains(e, StringComparison.OrdinalIgnoreCase) ||
                node.Name.Equals(e, StringComparison.OrdinalIgnoreCase));

            int firstHit = ranked.FindIndex(Match);
            sumP1 += firstHit == 0 ? 1 : 0;
            int relevantInTopK = ranked.Take(k).Count(Match);
            sumPk += (double)relevantInTopK / k;
            sumRecall += c.Expected.Count == 0 ? 0 : Math.Min(1.0, (double)relevantInTopK / c.Expected.Count);
            sumRr += firstHit >= 0 ? 1.0 / (firstHit + 1) : 0;
            n++;
        }

        return n == 0
            ? new MetricSet(0, 0, 0, 0, 0)
            : new MetricSet(n, sumP1 / n, sumPk / n, sumRecall / n, sumRr / n);
    }

    // Auto-bootstrap: one self-retrieval case per named symbol (Class/Interface/Record/Method/Enum).
    private static List<GoldenCase> Bootstrap(IReadOnlyList<GraphNode> nodes)
    {
        var symbolTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Class", "Interface", "Record", "Struct", "Enum", "Method" };
        return nodes
            .Where(n => symbolTypes.Contains(n.Type)
                        && !string.IsNullOrWhiteSpace(n.Name)
                        && n.Name.Length >= 4
                        && !n.Name.Contains(' '))
            .GroupBy(n => n.Name, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Take(200)
            .Select(g => new GoldenCase
            {
                Id = $"self-{g.Key}",
                Query = g.Key,
                Mode = "graph",
                Expected = g.Select(n => n.Id).Distinct().ToList()
            })
            .ToList();
    }

    private static void DumpSample(IReadOnlyList<GraphNode> nodes)
    {
        foreach (var type in new[] { "Class", "Interface", "Method", "File" })
        {
            var names = nodes.Where(n => n.Type.Equals(type, StringComparison.OrdinalIgnoreCase))
                             .Select(n => n.Name).Distinct().OrderBy(x => x).Take(30).ToList();
            Console.WriteLine($"--- {type} ({names.Count} shown) ---");
            Console.WriteLine(string.Join(", ", names));
            Console.WriteLine();
        }
    }

    private static IEmbeddingService? TryCreateEmbeddingService()
    {
        try
        {
            var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            var logger = LoggerFactory.Create(b => { }).CreateLogger<OllamaEmbeddingService>();
            return new OllamaEmbeddingService(new HttpClient(), config, logger);
        }
        catch { return null; }
    }

    private static ISemanticAnalyzer? TryCreateSemanticAnalyzer()
    {
        try
        {
            var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            var logger = LoggerFactory.Create(b => { }).CreateLogger<OllamaSemanticAnalyzer>();
            return new OllamaSemanticAnalyzer(new HttpClient(), config, logger);
        }
        catch { return null; }
    }

    private static int CountNodesWithEmbedding(string dbPath)
    {
        try
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly
                }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM Nodes WHERE Embedding IS NOT NULL;";
            return Convert.ToInt32(command.ExecuteScalar());
        }
        catch
        {
            return 0;
        }
    }

    private static string? ArgValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string Pct(int part, int total) => total == 0 ? "0%" : $"{100.0 * part / total:F1}%";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

internal sealed class GoldenCase
{
    public string Id { get; set; } = "";
    public string Query { get; set; } = "";
    /// <summary>"graph" (FTS, default), "semantic" (vector), or "ask" (RAG answer / grounding eval).</summary>
    public string? Mode { get; set; }
    /// <summary>Node-id substrings or exact names that count as a correct hit (retrieval) / must-cite (answer).</summary>
    public List<string> Expected { get; set; } = new();
    /// <summary>False for abstention cases: the answer is NOT in the graph and the system must say so.</summary>
    public bool IsAnswerable { get; set; } = true;
}

internal sealed record MetricSet(int Cases, double PrecisionAt1, double PrecisionAtK, double RecallAtK, double Mrr)
{
    public string Row(string label) =>
        $"| {label} | {Cases} | {PrecisionAt1:F3} {Ci(PrecisionAt1)} | {PrecisionAtK:F3} | {RecallAtK:F3} {Ci(RecallAtK)} | {Mrr:F3} |";

    /// <summary>95% confidence half-width (normal approximation) for a proportion metric, so a small
    /// golden set is read as a range, not a false-precision point estimate. Rendered as "±0.xxx".</summary>
    private string Ci(double p)
    {
        if (Cases <= 0) return string.Empty;
        var halfWidth = 1.96 * Math.Sqrt(Math.Max(p * (1 - p), 0) / Cases);
        return $"±{halfWidth:F3}";
    }
}
