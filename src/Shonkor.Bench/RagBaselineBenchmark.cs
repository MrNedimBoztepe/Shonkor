// Licensed to Shonkor under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Bench;

/// <summary>
/// Head-to-head against a naive "chunked-RAG without a graph" baseline. Both retrievers start from the SAME
/// embedding search (fair): the baseline splits every source file into fixed line-window chunks, embeds each,
/// and returns the top-k chunks; Shonkor uses the same query embedding to seed nodes, expands 2 hops over the
/// graph, and synthesizes a budgeted capsule. So this isolates the value ADDED by the graph + capsule budget
/// on top of plain vector retrieval. Reports, per side, the tokens delivered and whether the delivered context
/// actually COVERS the expected symbol (a retrieved chunk overlapping its span / the capsule's subgraph
/// containing its node). Chunk embeddings are cached by content hash so reruns are fast.
/// </summary>
internal static class RagBaselineBenchmark
{
    private const double CharsPerToken = 4.0;
    private const int WindowLines = 40;
    private const int TopK = 5;
    private const int Hops = 2;
    private const int BudgetChars = 12000;

    private sealed class Chunk
    {
        public required string File { get; init; }
        public int StartLine { get; init; }
        public int EndLine { get; init; }
        public required string Text { get; init; }
        public string Hash { get; init; } = "";
        public float[]? Embedding { get; set; }
    }

    public sealed record ComparisonResult(
        int Queries, int Chunks,
        double RagAvgTokens, double RagCoverage,
        double ShonkorAvgTokens, double ShonkorCoverage,
        double ShonkorSeedSurvival)
    {
        public double TokenSavingPct => RagAvgTokens > 0 ? (1.0 - ShonkorAvgTokens / RagAvgTokens) * 100 : 0;
    }

    public static async Task<ComparisonResult?> RunAsync(
        SqliteGraphStorageProvider provider, IReadOnlyList<GraphNode> nodes, IEmbeddingService? emb,
        List<GoldenCase> cases, ContextCapsuleSynthesizer synth, TextWriter log)
    {
        if (emb is null) { log.WriteLine("  rag baseline: no embedding backend — skipped"); return null; }

        var byId = nodes.ToDictionary(n => n.Id);

        var files = nodes
            .Where(n => n.Type == "File" && !string.IsNullOrEmpty(n.FilePath) && File.Exists(n.FilePath!))
            .Select(n => n.FilePath!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var chunks = BuildChunks(files);
        log.WriteLine($"  rag baseline: {chunks.Count} chunks from {files.Count} files; embedding (cached)...");

        await EmbedChunksAsync(chunks, emb, Path.Combine("bench", "rag-chunk-cache.json"), log);
        var embedded = chunks.Where(c => c.Embedding is { Length: > 0 }).ToList();
        if (embedded.Count == 0) { log.WriteLine("  rag baseline: no chunk embeddings — skipped"); return null; }

        double ragTok = 0, ragCov = 0, shTok = 0, shCov = 0, shSeedSurv = 0;
        var n = 0;
        foreach (var c in cases)
        {
            var qv = await emb.GenerateEmbeddingAsync(c.Query, EmbeddingKind.Query);

            // Shonkor first: same embedding → semantic seeds → 2-hop subgraph → budgeted capsule. Its token
            // count sets the budget for a FAIR baseline comparison (equal tokens, then compare coverage).
            var hits = await provider.SearchSemanticAsync(qv, TopK);
            var seeds = hits.Select(h => h.Node.Id).ToList();
            var (subNodes, subEdges) = seeds.Count > 0
                ? await provider.GetSubgraphAsync(seeds, Hops)
                : (new List<GraphNode>(), new List<GraphEdge>());
            var capsule = synth.Synthesize(subNodes, subEdges,
                new CapsuleOptions { SeedIds = seeds, MaxContentChars = BudgetChars, MaxNodes = 40 });
            var shTokQ = capsule.Length / CharsPerToken;
            shTok += shTokQ;

            // Coverage on the DELIVERED capsule TEXT (TICKET-202) — symmetric with the baseline, which
            // checks its delivered chunks. The pre-budget subgraph was an asymmetric, over-optimistic proxy:
            // the capsule budget can drop a node the subgraph contained.
            shCov += CapsuleCovers(capsule, c, byId) ? 1 : 0;
            // Seed survival: fraction of the semantic seeds whose node still appears in the delivered capsule.
            if (seeds.Count > 0)
                shSeedSurv += (double)seeds.Count(id => CapsuleMentions(capsule, byId.GetValueOrDefault(id))) / seeds.Count;

            // Baseline: take top chunks by cosine up to the SAME token budget, then compare coverage.
            var ranked = embedded
                .Select(ch => (ch, sim: Cosine(qv, ch.Embedding!)))
                .OrderByDescending(x => x.sim);
            double used = 0;
            var picked = new List<Chunk>();
            foreach (var (ch, _) in ranked)
            {
                var t = ch.Text.Length / CharsPerToken;
                if (picked.Count > 0 && used + t > shTokQ) break; // stay within Shonkor's budget (keep ≥1)
                picked.Add(ch);
                used += t;
                if (picked.Count >= 40) break;
            }
            ragTok += used;
            ragCov += picked.Any(ch => CoversExpected(ch, c, byId)) ? 1 : 0;
            n++;
        }

        return n == 0 ? null : new ComparisonResult(n, embedded.Count, ragTok / n, ragCov / n, shTok / n, shCov / n, shSeedSurv / n);
    }

    /// <summary>True when the delivered capsule TEXT mentions an expected node (by its name as a token).</summary>
    private static bool CapsuleCovers(string capsule, GoldenCase c, Dictionary<string, GraphNode> byId) =>
        c.Expected.Any(e => byId.TryGetValue(e, out var node) && CapsuleMentions(capsule, node));

    /// <summary>True when <paramref name="node"/>'s name appears as a whole word in the capsule text.</summary>
    private static bool CapsuleMentions(string capsule, GraphNode? node)
    {
        if (node is null || string.IsNullOrEmpty(node.Name)) return false;
        var idx = capsule.IndexOf(node.Name, StringComparison.Ordinal);
        while (idx >= 0)
        {
            var before = idx == 0 || !char.IsLetterOrDigit(capsule[idx - 1]);
            var afterPos = idx + node.Name.Length;
            var after = afterPos >= capsule.Length || !char.IsLetterOrDigit(capsule[afterPos]);
            if (before && after) return true;
            idx = capsule.IndexOf(node.Name, idx + 1, StringComparison.Ordinal);
        }
        return false;
    }

    private static List<Chunk> BuildChunks(List<string> files)
    {
        var chunks = new List<Chunk>();
        foreach (var f in files)
        {
            string[] lines;
            try { lines = File.ReadAllLines(f); } catch { continue; }
            for (var start = 0; start < lines.Length; start += WindowLines)
            {
                var take = Math.Min(WindowLines, lines.Length - start);
                var text = string.Join("\n", lines.Skip(start).Take(take));
                if (string.IsNullOrWhiteSpace(text)) continue;
                chunks.Add(new Chunk
                {
                    File = f,
                    StartLine = start,
                    EndLine = start + take - 1,
                    Text = text,
                    Hash = Sha(text)
                });
            }
        }
        return chunks;
    }

    private static async Task EmbedChunksAsync(List<Chunk> chunks, IEmbeddingService emb, string cachePath, TextWriter log)
    {
        var cache = new Dictionary<string, float[]>();
        if (File.Exists(cachePath))
        {
            try { cache = JsonSerializer.Deserialize<Dictionary<string, float[]>>(File.ReadAllText(cachePath)) ?? cache; }
            catch { /* stale/corrupt cache — rebuild */ }
        }

        var done = 0;
        var dirty = false;
        foreach (var c in chunks)
        {
            if (cache.TryGetValue(c.Hash, out var v)) { c.Embedding = v; continue; }
            try
            {
                c.Embedding = await emb.GenerateEmbeddingAsync(c.Text);
                if (c.Embedding is { Length: > 0 }) { cache[c.Hash] = c.Embedding; dirty = true; }
            }
            catch { /* skip an un-embeddable chunk */ }
            if (++done % 200 == 0) log.WriteLine($"    embedded {done} new chunk(s)...");
        }

        if (dirty)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cachePath))!);
            File.WriteAllText(cachePath, JsonSerializer.Serialize(cache));
        }
    }

    private static bool CoversExpected(Chunk ch, GoldenCase c, Dictionary<string, GraphNode> byId)
    {
        foreach (var e in c.Expected)
        {
            if (!byId.TryGetValue(e, out var node) || string.IsNullOrEmpty(node.FilePath)) continue;
            if (!string.Equals(node.FilePath, ch.File, StringComparison.OrdinalIgnoreCase)) continue;
            var ns = node.StartLine ?? 0;
            var ne = node.EndLine ?? ns;
            if (ns <= ch.EndLine && ch.StartLine <= ne) return true; // span overlap
        }
        return false;
    }

    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++) { dot += (double)a[i] * b[i]; na += (double)a[i] * a[i]; nb += (double)b[i] * b[i]; }
        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
