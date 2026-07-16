// Licensed to Shonkor under the MIT License.

using System.Numerics.Tensors;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
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

    /// <summary>One baseline chunk. <c>internal</c> so the pure ranking logic below can be unit-tested (#191).</summary>
    internal sealed class Chunk
    {
        public required string File { get; init; }
        public int StartLine { get; init; }
        public int EndLine { get; init; }
        public required string Text { get; init; }
        public string Hash { get; init; } = "";
        public float[]? Embedding { get; set; }
    }

    /// <param name="RagCoverage">Baseline coverage, chunks ranked VECTOR-ONLY.</param>
    /// <param name="RagHybridCoverage">Baseline coverage, chunks ranked HYBRID (BM25 + vector, RRF) — the baseline given Shonkor's retrieval minus the graph (#166).</param>
    /// <param name="ShonkorCoverage">Coverage when seeded VECTOR-ONLY — isolates the graph+capsule, both sides from identical retrieval.</param>
    /// <param name="ShonkorHybridCoverage">Coverage when seeded via <see cref="HybridRetrieval"/> — the path the product actually ships (#162).</param>
    /// <param name="SeedMissedTarget">Cases the capsule missed where the target was never a seed — i.e. a retrieval miss, not a budget casualty.</param>
    public sealed record ComparisonResult(
        int Queries, int Chunks,
        double RagAvgTokens, double RagCoverage,
        double RagHybridCoverage,
        double ShonkorAvgTokens, double ShonkorCoverage,
        double ShonkorSeedSurvival,
        double ShonkorHybridCoverage,
        int SeedMissedTarget,
        int RagKeywordFiredQueries)
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

        // Score chunks the way the product scores nodes (#163): normalize once, then dot. The storage layer
        // normalizes on write, so this is the same arithmetic — not a second, subtly different similarity.
        foreach (var ch in embedded) VectorMath.NormalizeL2(ch.Embedding!);

        // Keyword index over the chunks (#166), so the baseline's hybrid arm uses the SAME BM25 as the product.
        using var chunkFts = BuildChunkFts(embedded);

        double ragTok = 0, ragCov = 0, ragHybridCov = 0, shTok = 0, shCov = 0, shSeedSurv = 0, shHybridCov = 0;
        var seedMissedTarget = 0;
        var ragKeywordFired = 0; // #166: how often the baseline's keyword arm returned ANY hit at all
        var n = 0;
        foreach (var c in cases)
        {
            var qv = await emb.GenerateEmbeddingAsync(c.Query, EmbeddingKind.Query);
            var qn = VectorMath.NormalizedCopy(qv);

            // Shonkor: same embedding → semantic seeds → 2-hop subgraph → budgeted capsule. Its token count
            // sets the budget for a FAIR baseline comparison (equal tokens, then compare coverage).
            //
            // NOTE (#162): seeding VECTOR-ONLY here is what ISOLATES the graph+capsule contribution — both
            // sides start from the identical retrieval. But it is NOT what the product does: the shipped path
            // is HybridRetrieval (RRF of FTS + vector), which has materially better intent recall. So this arm
            // understates Shonkor-as-shipped. The hybrid arm below measures that, and both are reported —
            // picking whichever flatters us would be exactly the sin this benchmark exists to prevent.
            var hits = await provider.SearchSemanticAsync(qv, TopK);
            var seeds = hits.Select(h => h.Node.Id).ToList();
            var (subNodes, subEdges) = seeds.Count > 0
                ? await provider.GetSubgraphAsync(seeds, Hops)
                : (new List<GraphNode>(), new List<GraphEdge>());
            var capsule = synth.Synthesize(subNodes, subEdges,
                new CapsuleOptions { SeedIds = seeds, MaxContentChars = BudgetChars, MaxNodes = 40 });
            var shTokQ = capsule.Length / CharsPerToken;
            shTok += shTokQ;

            // The case's ground-truth nodes. Expected entries are id substrings OR bare names, so they must be
            // resolved with the shared GoldenMatch — an exact byId[expected] lookup silently matches nothing
            // for a name-based set, which is exactly how this metric came to report 0 % for both sides (#157).
            var expectedNodes = GoldenMatch.Resolve(nodes, c);

            // Coverage on the DELIVERED capsule TEXT (TICKET-202) — symmetric with the baseline, which
            // checks its delivered chunks. The pre-budget subgraph was an asymmetric, over-optimistic proxy:
            // the capsule budget can drop a node the subgraph contained.
            shCov += expectedNodes.Any(node => CapsuleMentions(capsule, node)) ? 1 : 0;
            // Seed survival: fraction of the semantic seeds whose node still appears in the delivered capsule.
            if (seeds.Count > 0)
                shSeedSurv += (double)seeds.Count(id => CapsuleMentions(capsule, byId.GetValueOrDefault(id))) / seeds.Count;

            // #162 diagnostic: when the capsule misses the target, was the target ever a SEED to begin with?
            // Seed survival is ~100 %, so a miss cannot be the budget dropping it — it means retrieval never
            // nominated it. This counts exactly that, so the conclusion is measured rather than argued.
            var expectedIds = expectedNodes.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            if (!expectedNodes.Any(node => CapsuleMentions(capsule, node)) && !seeds.Any(expectedIds.Contains))
                seedMissedTarget++;

            // The SHIPPED path (#162): HybridRetrieval — RRF of FTS + vector — is what search_hybrid,
            // generate_capsule, /api/search/hybrid and /api/capsule all use. Same budget, same capsule; only
            // the seeds differ. If this beats the vector-only arm, the benchmark was handicapping the product
            // against itself.
            var hybridSeeds = (await HybridRetrieval.SearchAsync(provider, emb, c.Query, TopK))
                .Select(h => h.Node.Id).ToList();
            var (hNodes, hEdges) = hybridSeeds.Count > 0
                ? await provider.GetSubgraphAsync(hybridSeeds, Hops)
                : (new List<GraphNode>(), new List<GraphEdge>());
            var hybridCapsule = synth.Synthesize(hNodes, hEdges,
                new CapsuleOptions { SeedIds = hybridSeeds, MaxContentChars = BudgetChars, MaxNodes = 40 });
            shHybridCov += expectedNodes.Any(node => CapsuleMentions(hybridCapsule, node)) ? 1 : 0;

            // Baseline arm 1 — VECTOR-ONLY: rank chunks by similarity, take up to the SAME token budget.
            var vectorRank = embedded
                .Select((ch, i) => (i, sim: Score(qn, ch.Embedding!)))
                .OrderByDescending(x => x.sim).Select(x => x.i).ToList();
            var (vecChunks, vecTokens) = PickWithinBudget(embedded, vectorRank, shTokQ);
            ragTok += vecTokens;
            ragCov += vecChunks.Any(ch => CoversExpected(ch, expectedNodes)) ? 1 : 0;

            // Baseline arm 2 — HYBRID (#166): the baseline gets the retrieval Shonkor gets, minus the graph.
            // Keyword (BM25 over the chunk texts) RRF-fused with the vector ranking, then the same budgeted
            // pick. This is the like-for-like counterpart to Shonkor-hybrid: the ONLY remaining difference
            // between ragHybridCov and shHybridCov is the graph. If they are equal, the graph adds ~0 on
            // coverage and the whole Shonkor-vs-baseline gap was hybrid retrieval, not the graph.
            var keywordRank = KeywordRankChunks(chunkFts, c.Query);
            if (keywordRank.Count > 0) ragKeywordFired++;
            var fusedRank = RrfFuse(vectorRank, keywordRank);
            var (hybChunks, _) = PickWithinBudget(embedded, fusedRank, shTokQ);
            ragHybridCov += hybChunks.Any(ch => CoversExpected(ch, expectedNodes)) ? 1 : 0;
            n++;
        }

        return n == 0
            ? null
            : new ComparisonResult(n, embedded.Count, ragTok / n, ragCov / n, ragHybridCov / n,
                shTok / n, shCov / n, shSeedSurv / n, shHybridCov / n, seedMissedTarget, ragKeywordFired);
    }

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

    /// <summary>
    /// True when the delivered chunk's file/line window overlaps one of the case's ground-truth nodes — the
    /// baseline's symmetric counterpart to <see cref="CapsuleMentions"/>. Takes the already-resolved nodes
    /// (see <see cref="GoldenMatch"/>); resolving by exact node id here was the #157 bug.
    /// </summary>
    private static bool CoversExpected(Chunk ch, IReadOnlyList<GraphNode> expectedNodes)
    {
        foreach (var node in expectedNodes)
        {
            if (string.IsNullOrEmpty(node.FilePath)) continue;
            if (!string.Equals(node.FilePath, ch.File, StringComparison.OrdinalIgnoreCase)) continue;
            var ns = node.StartLine ?? 0;
            var ne = node.EndLine ?? ns;
            if (ns <= ch.EndLine && ch.StartLine <= ne) return true; // span overlap
        }
        return false;
    }

    /// <summary>
    /// Scores a query against a chunk <b>exactly the way the product scores a node</b> (#163): both vectors are
    /// L2-normalized (<see cref="VectorMath"/>, on <c>TensorPrimitives</c>), so a dot product <i>is</i> the
    /// cosine — the identical arithmetic <c>SqliteGraphStorageProvider.SearchSemanticAsync</c> uses after
    /// normalize-on-write (TICKET-215).
    /// <para>
    /// This replaced a hand-rolled loop that accumulated in <c>double</c> while the storage path accumulates in
    /// SIMD <c>float</c>. Two summation semantics for one similarity is the exact inconsistency #127 removed
    /// from the product — having it back in the component whose whole job is trustworthy numbers meant the
    /// baseline was ranked by arithmetic the product does not use.
    /// </para>
    /// </summary>
    /// <summary>
    /// Walks a ranked chunk order and takes chunks up to <paramref name="budgetTokens"/> (keeping at least
    /// one, and at most 40) — the shared budgeted-pick both baseline arms use, so the vector and hybrid arms
    /// differ ONLY in their ranking, never in how the budget is applied.
    /// </summary>
    internal static (List<Chunk> Chunks, double Tokens) PickWithinBudget(
        IReadOnlyList<Chunk> chunks, List<int> order, double budgetTokens)
    {
        double used = 0;
        var picked = new List<Chunk>();
        foreach (var i in order)
        {
            var ch = chunks[i];
            var t = ch.Text.Length / CharsPerToken;
            if (picked.Count > 0 && used + t > budgetTokens) break;
            picked.Add(ch);
            used += t;
            if (picked.Count >= 40) break;
        }
        return (picked, used);
    }

    /// <summary>
    /// Builds an in-memory SQLite **FTS5** index over the chunk texts (#166), so the baseline can get a
    /// keyword arm that is the SAME BM25 the product uses — not a hand-rolled scorer (the #163 trap). Row i+1
    /// is chunk <c>embedded[i]</c>. The connection is kept open for the whole run and disposed by the caller.
    /// </summary>
    internal static SqliteConnection BuildChunkFts(IReadOnlyList<Chunk> chunks)
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using (var create = conn.CreateCommand())
        {
            create.CommandText = "CREATE VIRTUAL TABLE chunks USING fts5(text);";
            create.ExecuteNonQuery();
        }
        using var tx = conn.BeginTransaction();
        using (var insert = conn.CreateCommand())
        {
            insert.CommandText = "INSERT INTO chunks(rowid, text) VALUES (@r, @t);";
            var pr = insert.Parameters.Add("@r", SqliteType.Integer);
            var pt = insert.Parameters.Add("@t", SqliteType.Text);
            for (var i = 0; i < chunks.Count; i++)
            {
                pr.Value = i + 1;
                pt.Value = chunks[i].Text;
                insert.ExecuteNonQuery();
            }
        }
        tx.Commit();
        return conn;
    }

    /// <summary>
    /// Ranks chunks by BM25 for <paramref name="query"/>, best first, returning chunk indices into the
    /// embedded list. Mirrors the product's FTS path exactly, LIKE fallback and all: FTS5 <c>MATCH</c> ordered
    /// by <c>bm25()</c>, and on an FTS syntax error (colons, slashes, operators) it falls back to <c>LIKE</c>
    /// over the query's word tokens — the same degradation <c>SqliteGraphStorageProvider</c> does.
    /// </summary>
    internal static List<int> KeywordRankChunks(SqliteConnection conn, string query)
    {
        var order = new List<int>();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT rowid FROM chunks WHERE chunks MATCH @q ORDER BY bm25(chunks);";
            cmd.Parameters.AddWithValue("@q", query);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) order.Add((int)reader.GetInt64(0) - 1);
        }
        catch (SqliteException)
        {
            order.Clear();
            var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length > 2).Select(t => t.Trim()).Distinct().ToList();
            if (terms.Count == 0) return order;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT rowid FROM chunks WHERE " +
                string.Join(" OR ", terms.Select((_, i) => $"text LIKE @t{i}")) + ";";
            for (var i = 0; i < terms.Count; i++) cmd.Parameters.AddWithValue($"@t{i}", $"%{terms[i]}%");
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) order.Add((int)reader.GetInt64(0) - 1);
        }
        return order;
    }

    /// <summary>
    /// Reciprocal-rank fusion of two ranked chunk-index lists (#166), the SAME algorithm
    /// <see cref="HybridFusion"/> applies to Shonkor's node ranking: score(i) = Σ 1/(k + rank). So the
    /// baseline's keyword+vector fusion is like-for-like with the product's — the only remaining difference
    /// between the two hybrid rows is the graph.
    /// </summary>
    internal static List<int> RrfFuse(List<int> vectorRank, List<int> keywordRank, int k = 60)
    {
        var score = new Dictionary<int, double>();
        void Accumulate(List<int> ranked)
        {
            for (var rank = 0; rank < ranked.Count; rank++)
                score[ranked[rank]] = score.GetValueOrDefault(ranked[rank]) + 1.0 / (k + rank + 1);
        }
        Accumulate(vectorRank);
        Accumulate(keywordRank);
        return score.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
    }

    private static float Score(ReadOnlySpan<float> normalizedQuery, ReadOnlySpan<float> normalizedChunk) =>
        normalizedQuery.Length != normalizedChunk.Length ? 0f : TensorPrimitives.Dot(normalizedQuery, normalizedChunk);

    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
