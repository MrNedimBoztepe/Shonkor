// Licensed to Shonkor under the MIT License.

namespace Shonkor.Core.Models;

/// <summary>
/// A <c>Concept</c> node that still has no embedding, together with the names of the nodes it is connected
/// to. A concept carries no source body, so its name alone embeds to almost nothing usable; the connected
/// node names are what give it retrievable meaning ("idempotency" ← <c>PaymentProcessor</c>, <c>RetryPolicy</c>).
/// </summary>
/// <param name="Id">The concept node's id.</param>
/// <param name="Name">The concept name (e.g. "idempotency").</param>
/// <param name="Summary">The concept's AI summary, if one was ever written.</param>
/// <param name="ConnectedNames">Names of the nodes linked to this concept, bounded by the query.</param>
public sealed record ConceptEmbeddingCandidate(
    string Id,
    string Name,
    string? Summary,
    IReadOnlyList<string> ConnectedNames);
