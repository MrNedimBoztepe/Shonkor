// Licensed to Shonkor under the MIT License.

namespace Shonkor.Core.Interfaces;

/// <summary>
/// Aggregate contract for a full knowledge-graph storage backend, composing the segregated
/// <see cref="IGraphStore"/> (persistence), <see cref="IGraphSearch"/> (search/traversal) and
/// <see cref="ISemanticGraphStore"/> (AI enrichment) interfaces.
/// <para>
/// Prefer depending on the narrowest interface a consumer actually needs (e.g. a read-only search
/// endpoint should take <see cref="IGraphSearch"/>); use this aggregate only where the full surface
/// is genuinely required (e.g. the storage factory).
/// </para>
/// </summary>
public interface IGraphStorageProvider : IGraphStore, IGraphSearch, ISemanticGraphStore
{
}
