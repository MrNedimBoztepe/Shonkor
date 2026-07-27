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
    /// <summary>
    /// Definition/container node types that outrank their own members (Method/Property/Field) when — and
    /// only when — the RRF scores are EXACTLY equal (#343). On an exact-name query the user wants the
    /// defining symbol (the File/Class), not one of its members or tests; at a genuine tie that is the
    /// deterministic preference. This is a tie-break, not a weight: it never reorders nodes whose scores
    /// differ, so it is distinct from the type-aware score weighting declined in #110.
    /// </summary>
    private static readonly HashSet<string> DefinitionTypes = new(StringComparer.Ordinal)
    {
        "File", "Class", "Interface", "Record", "Struct", "Enum"
    };

    /// <summary>0 for a defining container, 1 otherwise — the ascending secondary sort key so containers win a tie.</summary>
    private static int TieBreakRank(SearchResult r) => DefinitionTypes.Contains(r.Node.Type) ? 0 : 1;

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
            // Deterministic tie-break, applied ONLY within an exact RRF-score tie (#343): prefer the defining
            // container over its members, then fall back to the node id. Step 2 is load-bearing, not cosmetic —
            // without it a tie is broken by the undefined Dictionary enumeration order, so an exact-name query
            // could return a member or a source file non-reproducibly.
            .ThenBy(kv => TieBreakRank(nodeById[kv.Key]))
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(maxResults)
            .Select(kv => nodeById[kv.Key] with { Score = kv.Value })
            .ToList();
    }
}
