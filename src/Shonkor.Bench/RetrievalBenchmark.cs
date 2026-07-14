// Licensed to Shonkor under the MIT License.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Bench;

/// <summary>
/// Retrieval precision over the graph: Precision@1, Precision@k, Recall@k, MRR for FTS (<c>search_graph</c>),
/// vector (<c>search_semantic</c>) and their RRF fusion (<c>search_hybrid</c> — the default mode) when an
/// Ollama embedding backend is reachable. The golden set auto-bootstraps a self-retrieval set from the DB
/// (query a symbol by its own name, expect itself) unless a curated set is supplied.
/// </summary>
internal static class RetrievalBenchmark
{
    public static async Task<(MetricSet Fts, MetricSet? Semantic, MetricSet? Hybrid)> RunAsync(
        SqliteGraphStorageProvider provider, IReadOnlyList<GraphNode> allNodes, int k, TextWriter log,
        List<GoldenCase>? goldenSet = null, string? setLabel = null)
    {
        // Every retriever is scored on the SAME cases, so a curated golden set (e.g. natural-language intent
        // queries) yields a clean FTS-vs-semantic-vs-hybrid comparison.
        var cases = goldenSet ?? Bootstrap(allNodes);
        log.WriteLine($"  golden cases: {cases.Count} ({setLabel ?? "auto-bootstrapped self-retrieval"}), k={k}");

        var fts = await Score(cases, k, c => provider.SearchAsync(c.Query, k));

        MetricSet? semantic = null, hybrid = null;
        var emb = TryCreateEmbeddingService();
        var reachable = false;
        if (emb is not null)
        {
            try { reachable = (await emb.GenerateEmbeddingAsync("probe", EmbeddingKind.Query)).Length > 0; }
            catch { reachable = false; }
        }

        if (reachable && emb is not null)
        {
            // Embed the QUERY with the query prefix (TICKET-202): the kind-less overload defaulted to the
            // Document prefix, so an earlier A/B "within noise" result measured document-prefixed queries.
            semantic = await Score(cases, k, async c =>
                await provider.SearchSemanticAsync(await emb.GenerateEmbeddingAsync(c.Query, EmbeddingKind.Query), k));

            // Hybrid = RRF of FTS + vector, exactly like the /api/search/hybrid endpoint (over-fetch k*2).
            hybrid = await Score(cases, k, async c =>
            {
                var ftsHits = await provider.SearchAsync(c.Query, k * 2);
                var qv = await emb.GenerateEmbeddingAsync(c.Query, EmbeddingKind.Query);
                var semHits = await provider.SearchSemanticAsync(qv, k * 2);
                return HybridFusion.ReciprocalRankFusion(ftsHits, semHits, k);
            });
        }
        else
        {
            log.WriteLine(emb is null
                ? "  semantic/hybrid: no embedding backend — skipped"
                : "  semantic/hybrid: embedding backend unreachable — skipped");
        }

        return (fts, semantic, hybrid);
    }

    private static async Task<MetricSet> Score(
        List<GoldenCase> cases, int k, Func<GoldenCase, Task<IReadOnlyList<SearchResult>>> retrieve)
    {
        double sumP1 = 0, sumPk = 0, sumRecall = 0, sumRr = 0;
        var n = 0;

        foreach (var c in cases)
        {
            var hits = await retrieve(c);
            // Drop the eval's own DEV-PROCESS META prose from the results before ranking (#110, #133). These
            // files describe the codebase in the same words a natural-language query uses, so they can outrank
            // the code they document — circular. bench/golden/*.json is the acute case (query strings
            // verbatim); tickets/, review/ and bench/*.md (this very measurement note included) are the
            // diffuse tail. Product code (src/) and product docs (docs/) are NOT filtered — the doc-intent
            // set is measured against docs/ and must stay retrievable.
            var ranked = hits.Select(h => h.Node).Where(n => !IsEvalMetaNode(n)).ToList();
            bool Match(GraphNode node) => GoldenMatch.Matches(node, c);

            var firstHit = ranked.FindIndex(Match);
            sumP1 += firstHit == 0 ? 1 : 0;
            var relevantInTopK = ranked.Take(k).Count(Match);
            sumPk += (double)relevantInTopK / k;
            sumRecall += c.Expected.Count == 0 ? 0 : Math.Min(1.0, (double)relevantInTopK / c.Expected.Count);
            sumRr += firstHit >= 0 ? 1.0 / (firstHit + 1) : 0;
            n++;
        }

        return n == 0 ? new MetricSet(0, 0, 0, 0, 0) : new MetricSet(n, sumP1 / n, sumPk / n, sumRecall / n, sumRr / n);
    }

    /// <summary>
    /// Whether a node is one of the project's DEV-PROCESS META documents, which must never count as a
    /// retrieval result because they describe the code in query vocabulary (self-reference → circular eval):
    /// <list type="bullet">
    /// <item><c>bench/golden/*.json</c> — the golden sets, which contain the query strings verbatim (#110);</item>
    /// <item><c>tickets/**</c>, <c>review/**</c> — process docs that paraphrase features in query words;</item>
    /// <item><c>bench/**/*.md</c> — measurement notes (this file included) that quote example queries (#133).</item>
    /// </list>
    /// Product CODE (<c>src/</c>) and product DOCS (<c>docs/</c>) are deliberately NOT excluded — the
    /// doc-intent golden set is scored against <c>docs/</c> sections and must stay retrievable. Path-separator
    /// agnostic (holds on Windows and Unix). Kept in the graph for agents; only the EVAL ignores them.
    /// </summary>
    internal static bool IsEvalMetaNode(GraphNode node)
    {
        var path = node.FilePath;
        if (string.IsNullOrEmpty(path)) return false;
        var p = path.Replace('\\', '/');

        bool Under(string dir) =>
            p.Contains("/" + dir + "/", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase);

        if (Under("bench/golden") || Under("tickets") || Under("review")) return true;
        // bench measurement notes (prose) — but NOT bench source code (.cs).
        return Under("bench") && p.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
    }

    // One self-retrieval case per named symbol (Class/Interface/Record/Struct/Enum/Method).
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

    private static IEmbeddingService? TryCreateEmbeddingService()
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
}
