using System.Threading;
using System.Threading.Tasks;

namespace Shonkor.Core.Interfaces;

/// <summary>Whether the text being embedded is an indexed document or a search query. Some embedding
/// models (e.g. nomic-embed-text) are trained with task prefixes and retrieve better when query and
/// document are embedded under their respective prefix.</summary>
public enum EmbeddingKind
{
    Document,
    Query
}

public interface IEmbeddingService
{
    Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Kind-aware embedding. Default implementation ignores <paramref name="kind"/> and delegates to
    /// <see cref="GenerateEmbeddingAsync(string, CancellationToken)"/>, so existing implementations keep
    /// working unchanged; backends that benefit from task prefixes override this.
    /// </summary>
    Task<float[]> GenerateEmbeddingAsync(string text, EmbeddingKind kind, CancellationToken cancellationToken = default)
        => GenerateEmbeddingAsync(text, cancellationToken);
}
