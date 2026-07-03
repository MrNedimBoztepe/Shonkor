// Licensed to Shonkor under the MIT License.
//
// In-process retrieval experiment used to produce REAL before/after precision numbers for the
// roadmap (review/roadmap.md) without mutating the production DB. It calls the live embedding
// backend (Ollama) directly, embeds the symbol-node candidate pool under a chosen source
// (summary | code | name), and scores the intent golden set under three retrievers:
//   - graph    : FTS5 (the shipped baseline)
//   - semantic : cosine over the chosen embedding source
//   - hybrid   : Reciprocal Rank Fusion of FTS + semantic (TICKET-008)
// Embeddings are cached on disk (eval/cache-*.json) so re-runs are fast.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Infrastructure.Storage;

internal static class SemanticExperiment
{
    private static readonly HashSet<string> SymbolTypes = new(StringComparer.OrdinalIgnoreCase)
        { "Class", "Interface", "Record", "Struct", "Enum", "Method", "Constructor" };

    public static async Task RunAsync(
        SqliteGraphStorageProvider provider,
        IReadOnlyList<GraphNode> allNodes,
        IEmbeddingService embedding,
        List<GoldenCase> cases,
        int k,
        string source,        // summary | code | name
        bool usePrefix)
    {
        var pool = allNodes.Where(n => SymbolTypes.Contains(n.Type) && !string.IsNullOrWhiteSpace(n.Name)).ToList();
        Console.WriteLine($"[experiment] source={source} prefix={usePrefix} pool={pool.Count} symbols, cases={cases.Count}, k={k}");

        // Build (or load) the document embeddings for the candidate pool.
        var cachePath = Path.Combine("eval", $"cache-{source}-{(usePrefix ? "px" : "nopx")}.json");
        var cache = LoadCache(cachePath);
        var docVectors = new Dictionary<string, float[]>();
        int built = 0, skipped = 0;

        foreach (var node in pool)
        {
            var text = DocText(node, source);
            if (string.IsNullOrWhiteSpace(text)) { skipped++; continue; }
            var key = Hash(source + "|" + (usePrefix ? "px" : "0") + "|" + text);
            if (!cache.TryGetValue(key, out var vec))
            {
                var input = usePrefix ? "search_document: " + text : text;
                vec = await embedding.GenerateEmbeddingAsync(input);
                if (vec.Length == 0) { skipped++; continue; }
                cache[key] = vec;
                built++;
                if (built % 50 == 0) Console.WriteLine($"[experiment]   embedded {built} docs...");
            }
            docVectors[node.Id] = vec;
        }
        SaveCache(cachePath, cache);
        Console.WriteLine($"[experiment] doc embeddings ready: {docVectors.Count} (newly built {built}, skipped {skipped})");

        var byId = pool.ToDictionary(n => n.Id);

        double sP1s = 0, sRks = 0, sMrrS = 0;  // semantic
        double sP1h = 0, sRkh = 0, sMrrH = 0;  // hybrid
        int n = 0;

        foreach (var c in cases)
        {
            // Semantic ranking
            var qinput = usePrefix ? "search_query: " + c.Query : c.Query;
            var qv = await embedding.GenerateEmbeddingAsync(qinput);
            var semRanked = docVectors
                .Select(kv => (Id: kv.Key, Score: Cosine(qv, kv.Value)))
                .OrderByDescending(x => x.Score)
                .Select(x => x.Id)
                .ToList();

            // FTS ranking (restricted to the symbol pool for a fair fusion)
            var ftsHits = await provider.SearchAsync(c.Query, 50);
            var ftsRanked = ftsHits.Select(h => h.Node.Id).Where(byId.ContainsKey).ToList();

            // Hybrid = Reciprocal Rank Fusion (k0=60), the TICKET-008 design.
            var hybridRanked = Rrf(ftsRanked, semRanked, 60).Take(k * 5).ToList();

            (sP1s, sRks, sMrrS) = Accumulate(sP1s, sRks, sMrrS, semRanked, c, k, byId);
            (sP1h, sRkh, sMrrH) = Accumulate(sP1h, sRkh, sMrrH, hybridRanked, c, k, byId);
            n++;
        }

        Console.WriteLine();
        Console.WriteLine($"| semantic ({source}{(usePrefix ? "+prefix" : "")}) | {n} | {sP1s / n:F3} | — | {sRks / n:F3} | {sMrrS / n:F3} |");
        Console.WriteLine($"| hybrid RRF (FTS + {source}) | {n} | {sP1h / n:F3} | — | {sRkh / n:F3} | {sMrrH / n:F3} |");
        Console.WriteLine();

        var outFile = Path.Combine("eval", $"experiment-{source}{(usePrefix ? "-px" : "")}.json");
        File.WriteAllText(outFile, JsonSerializer.Serialize(new
        {
            source, usePrefix, cases = n, k,
            semantic = new { p1 = sP1s / n, recallK = sRks / n, mrr = sMrrS / n },
            hybrid = new { p1 = sP1h / n, recallK = sRkh / n, mrr = sMrrH / n }
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"[experiment] wrote {outFile}");
    }

    private static (double, double, double) Accumulate(
        double p1, double rk, double mrr, List<string> ranked, GoldenCase c, int k,
        Dictionary<string, GraphNode> byId)
    {
        bool Match(string id) =>
            byId.TryGetValue(id, out var node) &&
            c.Expected.Any(e => id.Contains(e, StringComparison.OrdinalIgnoreCase) ||
                                node.Name.Equals(e, StringComparison.OrdinalIgnoreCase));
        int first = ranked.FindIndex(Match);
        p1 += first == 0 ? 1 : 0;
        rk += ranked.Take(k).Any(Match) ? 1 : 0;
        mrr += first >= 0 ? 1.0 / (first + 1) : 0;
        return (p1, rk, mrr);
    }

    // Reciprocal Rank Fusion: score(d) = sum_r 1/(k0 + rank_r(d)).
    private static List<string> Rrf(List<string> a, List<string> b, int k0)
    {
        var scores = new Dictionary<string, double>();
        void Add(List<string> list) { for (int i = 0; i < list.Count; i++) scores[list[i]] = scores.GetValueOrDefault(list[i]) + 1.0 / (k0 + i + 1); }
        Add(a); Add(b);
        return scores.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
    }

    // Build the embedding document via the SAME production helper (EmbeddingTextBuilder) so the eval
    // measures exactly what ships — incl. the head+tail bounding (TICKET-105) — instead of a divergent
    // head-only copy. "name" stays a probe-only mode for ablation.
    private static string DocText(GraphNode node, string source) => source switch
    {
        "name" => node.Name,
        _ => Shonkor.Core.Services.EmbeddingTextBuilder.Build(node, node.Summary, source)
    };

    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length == 0 || a.Length != b.Length) return -1;
        return System.Numerics.Tensors.TensorPrimitives.CosineSimilarity(a, b);
    }

    private static string Hash(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes, 0, 12);
    }

    private static Dictionary<string, float[]> LoadCache(string path)
    {
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, float[]>>(File.ReadAllText(path)) ?? new(); }
        catch { return new(); }
    }

    private static void SaveCache(string path, Dictionary<string, float[]> cache)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(cache));
    }
}
