// Licensed to Shonkor under the MIT License.

using System.Security.Cryptography;
using System.Text;

namespace Shonkor.Core.Services;

/// <summary>
/// What an agent-authored edge knew about its anchor when it was written, so a later reader can be told
/// that the anchor has moved on — without anyone re-deriving the edge to find out.
///
/// <para>
/// The measured reason it exists (#434): asked twice with byte-identical input, the concept generator
/// reproduced its own answer for <b>8 of 48</b> nodes; 68 % of targets came back different. So
/// re-deriving an assignment is not a correction, it is a second sample. An automatic refresh would
/// replace a stored assignment with a differently-wrong one and present it as current, which is why
/// divergence here is <b>information for a human, not a trigger</b>.
/// </para>
///
/// <para>
/// The stamp itself is opaque, like the toolchain fingerprint (#408): it is a value to compare, never a
/// value to parse. What goes into it can change — prompt version, a second model, a content window —
/// without any consumer noticing, because nobody may read a part out of it. The model id is stored
/// alongside in the clear for exactly one reason: a reader has to be able to recompute the stamp for the
/// current anchor, and it cannot do that from an opaque string.
/// </para>
/// </summary>
public static class SourceStateStamp
{
    /// <summary>Edge property holding the opaque stamp.</summary>
    public const string StateKey = "sourceState";

    /// <summary>Edge property holding the producing model's id, in the clear so the stamp is recomputable.</summary>
    public const string ModelKey = "sourceModel";

    /// <summary>
    /// The stamp for an anchor's content hash under a given model. Both parts are required: the same code
    /// analyzed by a different model is a different assertion, and the same model over changed code is too.
    /// </summary>
    public static string Compute(string? anchorContentHash, string? model)
    {
        var bytes = Encoding.UTF8.GetBytes($"{anchorContentHash ?? string.Empty}\n{model ?? string.Empty}");
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>The two properties to persist with an edge written against <paramref name="anchorContentHash"/>.</summary>
    public static Dictionary<string, string> For(string? anchorContentHash, string? model) => new()
    {
        [StateKey] = Compute(anchorContentHash, model),
        [ModelKey] = model ?? string.Empty,
    };

    /// <summary>
    /// Whether the anchor has changed since the edge was written. <c>null</c> means "cannot tell" — an edge
    /// with no stamp (written before this existed, or by a producer that sets none), which must not be
    /// reported as either current or diverged. Absence of evidence gets its own answer here because the
    /// alternative is a graph where "no stamp" silently reads as "fine".
    /// </summary>
    public static bool? IsDiverged(IReadOnlyDictionary<string, string>? edgeProperties, string? currentAnchorContentHash)
    {
        if (edgeProperties is null) return null;
        if (!edgeProperties.TryGetValue(StateKey, out var stamped) || string.IsNullOrEmpty(stamped)) return null;
        edgeProperties.TryGetValue(ModelKey, out var model);
        return !string.Equals(stamped, Compute(currentAnchorContentHash, model), StringComparison.OrdinalIgnoreCase);
    }
}
