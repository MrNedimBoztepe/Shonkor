using System.Threading;
using System.Threading.Tasks;

namespace Shonkor.Core.Interfaces;

public interface IEmbeddingService
{
    Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default);
}
