// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Services;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Benchmarks;

/// <summary>
/// Honest token benchmark of the REAL shipped retrieval path (TICKET-007). For each seed query it runs
/// the same pipeline the RAG/capsule endpoints use — FTS search → N-hop subgraph → capsule synthesis —
/// and compares the <b>budget-aware capsule</b> (TICKET-003) against the <b>naive full-content dump</b>
/// of the very same retrieved subgraph (what an unbudgeted "send everything" RAG would emit).
/// This measures what Shonkor actually ships, not a whole-file-vs-1-sentence strawman.
/// </summary>
internal static class Program
{
    private const double CharsPerToken = 4.0;
    private const int SeedHits = 5;
    private const int Hops = 2;
    private const int BudgetChars = 12000; // ~3k tokens of code — matches the endpoints.

    private static readonly string[] DefaultQueries =
    {
        "context capsule synthesizer", "ollama embedding service", "sqlite graph storage provider",
        "roslyn ast parser", "api key middleware", "cross technology linker", "semantic enrichment"
    };

    private static async Task<int> Main(string[] args)
    {
        var dbPath = args.FirstOrDefault(a => !a.StartsWith("--"))
                     ?? Environment.GetEnvironmentVariable("SHONKOR_BENCHMARK_DB")
                     ?? "shonkor.db";

        if (!File.Exists(dbPath))
        {
            Console.Error.WriteLine($"[Error] Database not found at '{dbPath}'.");
            return 1;
        }

        var provider = new SqliteGraphStorageProvider(dbPath);
        await provider.InitializeAsync();
        var synthesizer = new ContextCapsuleSynthesizer();

        Console.WriteLine("=== Shonkor Capsule Token Benchmark (real shipped path) ===");
        Console.WriteLine($"DB: {Path.GetFileName(dbPath)} | seeds/query: {SeedHits} | hops: {Hops} | budget: ~{BudgetChars / 4} tokens\n");

        long totalNaive = 0, totalBudget = 0;
        int counted = 0;

        foreach (var query in DefaultQueries)
        {
            var hits = await provider.SearchAsync(query, SeedHits);
            if (hits.Count == 0) { Console.WriteLine($"  '{query}': no hits — skipped"); continue; }

            var seeds = hits.Select(h => h.Node.Id).ToList();
            var (nodes, edges) = await provider.GetSubgraphAsync(seeds, Hops);

            // Naive baseline: every node's full content concatenated (the old unbudgeted behaviour).
            long naiveChars = nodes.Sum(n => (long)(n.Content?.Length ?? 0));

            // Real shipped capsule: seed-first, budget-aware.
            var capsule = synthesizer.Synthesize(nodes, edges,
                new CapsuleOptions { SeedIds = seeds, MaxContentChars = BudgetChars, MaxNodes = 40 });
            long capsuleChars = capsule.Length;

            totalNaive += naiveChars;
            totalBudget += capsuleChars;
            counted++;

            var saving = naiveChars > 0 ? (1.0 - (double)capsuleChars / naiveChars) * 100 : 0;
            Console.WriteLine($"  '{query}': {nodes.Count,3} nodes | naive {Tok(naiveChars),7:N0} tok → capsule {Tok(capsuleChars),6:N0} tok  ({saving,5:F1}% )");
        }

        if (counted == 0) { Console.WriteLine("\nNo queries produced hits."); return 0; }

        var avgSaving = totalNaive > 0 ? (1.0 - (double)totalBudget / totalNaive) * 100 : 0;
        Console.WriteLine("\n--- AGGREGATE ---");
        Console.WriteLine($"Naive full-dump:  {Tok(totalNaive):N0} tokens");
        Console.WriteLine($"Budgeted capsule: {Tok(totalBudget):N0} tokens");
        Console.WriteLine($"Token reduction:  {avgSaving:F1}%  (over {counted} queries, vs the same retrieved subgraph)");
        Console.WriteLine("\nNote: this compares Shonkor's budgeted capsule to dumping the SAME retrieved nodes in full.");
        Console.WriteLine("It does NOT claim a comparison to whole-repo or chunked-RAG baselines.");
        return 0;
    }

    private static long Tok(long chars) => (long)(chars / CharsPerToken);
}
