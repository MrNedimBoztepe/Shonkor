// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Models;

namespace Shonkor.Core.Services;

/// <summary>
/// Reciprocal Rank Fusion (RRF) of multiple ranked result lists — the deterministic, offline-friendly
/// way to combine FTS (BM25) and vector similarity without tuning score scales (TICKET-008).
/// score(d) = Σ_list 1 / (k0 + rank_list(d)); k0 dampens the influence of very high ranks (default 60).
/// </summary>
public static class HybridFusion
{
    public static IReadOnlyList<SearchResult> ReciprocalRankFusion(
        IReadOnlyList<SearchResult> primary,
        IReadOnlyList<SearchResult> secondary,
        int maxResults,
        int k0 = 60)
    {
        var fused = new Dictionary<string, double>();
        var nodeById = new Dictionary<string, SearchResult>();

        void Accumulate(IReadOnlyList<SearchResult> list)
        {
            for (var rank = 0; rank < list.Count; rank++)
            {
                var r = list[rank];
                fused[r.Node.Id] = fused.GetValueOrDefault(r.Node.Id) + 1.0 / (k0 + rank + 1);
                // Prefer the richer SearchResult (one carrying edges) when the same node appears twice.
                if (!nodeById.TryGetValue(r.Node.Id, out var existing) || existing.RelatedEdges.Count < r.RelatedEdges.Count)
                {
                    nodeById[r.Node.Id] = r;
                }
            }
        }

        Accumulate(primary);
        Accumulate(secondary);

        return fused
            .OrderByDescending(kv => kv.Value)
            .Take(maxResults)
            .Select(kv => nodeById[kv.Key] with { Score = kv.Value })
            .ToList();
    }
}
