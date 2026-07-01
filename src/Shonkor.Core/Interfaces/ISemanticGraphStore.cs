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
    /// Updates the semantic summary of a node and resets its pending flag.
    /// </summary>
    Task UpdateNodeSemanticDataAsync(string nodeId, SemanticAnalysisResult result, float[]? embedding = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Flags stored embeddings whose dimension differs from <paramref name="expectedDim"/> for re-embedding
    /// (clears the vector and re-marks the node as pending), instead of letting the vector search silently
    /// skip them forever after a model/dimension change. Returns the number of nodes flagged. Legacy
    /// embeddings with an unknown (null) dimension are left untouched.
    /// </summary>
    Task<int> MarkStaleEmbeddingsForReembedAsync(int expectedDim, CancellationToken cancellationToken = default);
}
