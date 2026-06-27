// Licensed to Shonkor under the MIT License.

namespace Shonkor.Core.Models;

/// <summary>
/// Host-supplied context for a phase-2 post-processor run. Additive by design: a post-processor that
/// ignores it behaves exactly as before. Carries per-project configuration the host has resolved, so a
/// plugin need not (and cannot) reach into host settings itself.
/// </summary>
public sealed class GraphPostProcessorContext
{
    /// <summary>A reusable context with no project configuration (the default when none is supplied).</summary>
    public static GraphPostProcessorContext Empty { get; } = new();

    /// <summary>
    /// Extra namespace prefixes the user declared as external/third-party for this project. A post-processor
    /// merges these with its own built-in list (e.g. the Sitecore clrtype resolver's framework prefixes), so
    /// unresolved references to them are downgraded from Warning to Info instead of being flagged as missing
    /// code. A trailing dot scopes the match to a namespace — e.g. <c>"Dianoga."</c> matches
    /// <c>Dianoga.*</c> but not <c>DianogaLike</c>.
    /// </summary>
    public IReadOnlyList<string> ExternalTypePrefixes { get; init; } = System.Array.Empty<string>();
}
