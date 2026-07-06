// Licensed to Shonkor under the MIT License.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Infrastructure.Services;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Bench;

/// <summary>
/// Retrieval precision over the graph: Precision@1, Precision@k, Recall@k, MRR for FTS (<c>search_graph</c>)
/// and — when an Ollama embedding backend is reachable — vector (<c>search_semantic</c>). The golden set
/// auto-bootstraps a self-retrieval set from the DB (query a symbol by its own name, expect itself), which
/// honestly measures ranking/tokenisation quality without hand-curated fixtures.
/// </summary>
internal static class RetrievalBenchmark
{
    public static async Task<(MetricSet Fts, MetricSet? Semantic)> RunAsync(
        SqliteGraphStorageProvider provider, IReadOnlyList<GraphNode> allNodes, int k, TextWriter log,
        List<GoldenCase>? goldenSet = null, string? setLabel = null)
    {
        // Both retrievers are scored on the SAME cases (per-case Mode is ignored), so a curated golden set
        // (e.g. natural-language intent queries) yields a clean FTS-vs-semantic comparison.
        var cases = goldenSet ?? Bootstrap(allNodes);
        log.WriteLine($"  golden cases: {cases.Count} ({setLabel ?? "auto-bootstrapped self-retrieval"}), k={k}");

        var fts = await Score(provider, embedding: null, cases, k, semantic: false);

        MetricSet? semantic = null;
        var emb = TryCreateEmbeddingService();
        if (emb is not null)
        {
            var reachable = false;
            try { reachable = (await emb.GenerateEmbeddingAsync("probe")).Length > 0; }
            catch { reachable = false; }
            if (reachable)
            {
                semantic = await Score(provider, emb, cases, k, semantic: true);
            }
            else
            {
                log.WriteLine("  semantic: embedding backend unreachable — skipped");
            }
        }
        else
        {
            log.WriteLine("  semantic: no embedding backend — skipped");
        }

        return (fts, semantic);
    }

    private static async Task<MetricSet> Score(
        SqliteGraphStorageProvider provider, IEmbeddingService? embedding,
        List<GoldenCase> cases, int k, bool semantic)
    {
        double sumP1 = 0, sumPk = 0, sumRecall = 0, sumRr = 0;
        var n = 0;

        foreach (var c in cases)
        {
            IReadOnlyList<SearchResult> hits;
            if (semantic)
            {
                if (embedding is null) continue;
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
