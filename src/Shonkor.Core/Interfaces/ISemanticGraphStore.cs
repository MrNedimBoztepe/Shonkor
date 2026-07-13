// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Models;

namespace Shonkor.Core.Interfaces;

/// <summary>
/// AI-enrichment side of the graph store: discovering nodes that still need a semantic summary
/// and writing back the analysis result and embedding. Isolated so the background enrichment worker
/// can depend on only this slice, not the whole storage surface.
/// </summary>
public interface ISemanticGraphStore
{
    /// <summary>
    /// Retrieves nodes that are pending semantic analysis.
    /// </summary>
    Task<IReadOnlyList<GraphNode>> GetNodesPendingSemanticAnalysisAsync(int batchSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the semantic summary of a node and resets its pending flag. <paramref name="embeddingModel"/>
    /// (when an embedding is supplied) records which model produced the vector, so a later model change is
    /// detectable even at an unchanged dimension.
    /// </summary>
    Task UpdateNodeSemanticDataAsync(string nodeId, SemanticAnalysisResult result, float[]? embedding = null, string? embeddingModel = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Flags stored embeddings that no longer match the current model for re-embedding (clears the vector and
    /// re-marks the node as pending), instead of letting the vector search silently skip or mis-rank them
    /// after a model/dimension change. A vector is stale if its stored dimension differs from
    /// <paramref name="expectedDim"/>, or — when <paramref name="expectedModel"/> is given — its stored model
    /// differs (catches a same-dimension model swap). Returns the number flagged; embeddings whose
    /// dimension/model metadata is unknown (null) are left untouched.
    /// </summary>
    Task<int> MarkStaleEmbeddingsForReembedAsync(int expectedDim, string? expectedModel = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes only a node's embedding vector (and its dimension + <paramref name="embeddingModel"/>), without
    /// touching its summary, concepts or pending flag. Used by the CLI embed pass, which populates vectors for
    /// semantic/hybrid search without running LLM summarization. A <c>null</c>/empty vector clears the embedding.
    /// </summary>
    Task UpdateNodeEmbeddingAsync(string nodeId, float[]? embedding, string? embeddingModel = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves <c>Concept</c> nodes that still carry no embedding, each with the names of its connected
    /// nodes. Concepts are deliberately excluded from semantic ANALYSIS (they have no body to summarize) —
    /// which is why they also never got embedded and were invisible to semantic search. The pending predicate
    /// here is simply "no embedding yet", so the pass is self-terminating and needs no pending flag.
    /// </summary>
    Task<IReadOnlyList<ConceptEmbeddingCandidate>> GetConceptsPendingEmbeddingAsync(int batchSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes <c>Concept</c> nodes that have no incoming <c>RELATES_TO</c> edge — concepts whose every
    /// referencing code node was removed (a deleted/re-indexed file). Concepts carry no <c>FilePath</c>, so
    /// the path-based reindex cleanup never touches them; without this they accumulate as orphans and, since
    /// they are embedded, pollute semantic search. Must be called only when the project's current code is
    /// fully enriched (nothing pending), so a concept merely mid-reindex isn't mistaken for stale. Returns
    /// the number deleted.
    /// </summary>
    Task<int> PruneOrphanConceptsAsync(CancellationToken cancellationToken = default);
}
