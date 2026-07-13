// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;

namespace Shonkor.Core.Services;

/// <summary>
/// The single hybrid-retrieval entry point (#122): keyword (FTS/BM25) results fused with vector-similarity
/// results via reciprocal-rank fusion, degrading to FTS-only when no embedding backend is wired or the
/// embedding call fails. Lives in Core (all its dependencies do) so every layer shares ONE implementation —
/// the MCP tools (<c>search_hybrid</c>, capsule seeding) and the web endpoints (<c>/api/search/hybrid</c>,
/// <c>/api/capsule</c>) all call this, instead of each keeping its own copy that can drift.
/// </summary>
public static class HybridRetrieval
{
    /// <summary>
    /// Returns the best <paramref name="count"/> matches for <paramref name="query"/>. Each retriever is
    /// over-fetched (<c>count * 2</c>) before fusion so the RRF has candidates to combine. A null
    /// <paramref name="embeddingService"/> — or an embedding backend hiccup — yields FTS-only results rather
    /// than failing the query.
    /// </summary>
    public static async Task<IReadOnlyList<SearchResult>> SearchAsync(
        IGraphSearch storage,
        IEmbeddingService? embeddingService,
        string query,
        int count,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);

        var ftsResults = await storage.SearchAsync(query, count * 2, 0, null, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<SearchResult> semResults = [];
        if (embeddingService is not null)
        {
            try
            {
                var embedding = await embeddingService.GenerateEmbeddingAsync(query, EmbeddingKind.Query, cancellationToken).ConfigureAwait(false);
                if (embedding is { Length: > 0 })
                {
                    semResults = await storage.SearchSemanticAsync(embedding, count * 2, cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                // Embedding backend unreachable/slow — fall back to FTS-only fusion rather than failing.
            }
        }

        return HybridFusion.ReciprocalRankFusion(ftsResults, semResults, count);
    }
}
