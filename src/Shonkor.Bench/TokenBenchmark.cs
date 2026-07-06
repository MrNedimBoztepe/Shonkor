// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Services;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Bench;

/// <summary>
/// Honest token benchmark of the REAL shipped retrieval path: for each seed query it runs the same
/// pipeline the RAG/capsule endpoints use — FTS search → N-hop subgraph → capsule synthesis — and
/// compares the budget-aware capsule against the naive full-content dump of the SAME retrieved subgraph
/// (what an unbudgeted "send everything" RAG would emit). Not a whole-file-vs-1-sentence strawman.
/// </summary>
internal static class TokenBenchmark
{
    private const double CharsPerToken = 4.0;
    private const int SeedHits = 5;
    private const int Hops = 2;
    private const int BudgetChars = 12000; // ~3k tokens of code — matches the endpoints.

    private static readonly string[] Queries =
    {
        "context capsule synthesizer", "ollama embedding service", "sqlite graph storage provider",
        "roslyn ast parser", "api key middleware", "cross technology linker", "semantic enrichment"
    };

    public static async Task<TokenResult> RunAsync(SqliteGraphStorageProvider provider, ContextCapsuleSynthesizer synth, TextWriter log)
    {
        long totalNaive = 0, totalCapsule = 0;
        var counted = 0;

        foreach (var query in Queries)
        {
            var hits = await provider.SearchAsync(query, SeedHits);
            if (hits.Count == 0) { log.WriteLine($"  '{query}': no hits — skipped"); continue; }

            var seeds = hits.Select(h => h.Node.Id).ToList();
            var (nodes, edges) = await provider.GetSubgraphAsync(seeds, Hops);

            var naiveChars = nodes.Sum(n => (long)(n.Content?.Length ?? 0));
            var capsule = synth.Synthesize(nodes, edges,
                new CapsuleOptions { SeedIds = seeds, MaxContentChars = BudgetChars, MaxNodes = 40 });

            totalNaive += naiveChars;
            totalCapsule += capsule.Length;
            counted++;

            var saving = naiveChars > 0 ? (1.0 - (double)capsule.Length / naiveChars) * 100 : 0;
            log.WriteLine($"  '{query}': {nodes.Count,3} nodes | naive {Tok(naiveChars),7:N0} → capsule {Tok(capsule.Length),6:N0} tok  ({saving,5:F1}%)");
        }

        return new TokenResult(counted, Tok(totalNaive), Tok(totalCapsule));
    }

    private static long Tok(long chars) => (long)(chars / CharsPerToken);
}
