// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Models;

namespace Shonkor.Core.Interfaces;

/// <summary>
/// A graph-aware "phase 2" plugin extension. After the per-file <see cref="IFileParser"/> pass has
/// assembled the graph, each active post-processor runs once with a read-only view of the WHOLE graph
/// and returns additive enrichment (extra nodes/edges) plus diagnostics. This enables cross-file features
/// a per-file parser cannot do: reference resolution, type-aware links, architectural rule checks, and
/// unresolved-reference diagnostics.
/// </summary>
/// <remarks>
/// CONTRACT (v1): <b>additive only</b> — a post-processor may ADD nodes/edges and emit diagnostics, but
/// must not rely on mutating or removing what phase 1 produced. All post-processors observe the same
/// phase-1 snapshot (never each other's output), so the result is order-independent. Failures are isolated
/// like <see cref="IFileParser"/>: a throwing post-processor is skipped, the rest still run.
/// </remarks>
public interface IGraphPostProcessor
{
    /// <summary>Stable name for diagnostics/telemetry/UI, e.g. <c>"sitecore.clrtype-resolver"</c>.</summary>
    string Name { get; }

    /// <summary>
    /// The baseline trust tier of the edges this post-processor produces, applied by the host exactly like
    /// <see cref="IFileParser.DefaultProvenance"/>: each produced edge is stamped with the more-uncertain of
    /// this baseline and the edge's own <see cref="GraphEdge.Provenance"/>, so a post-processor that forgets
    /// to tag an individual edge cannot over-claim, while it can still escalate a specific edge to
    /// <see cref="Provenance.Ambiguous"/>.
    /// <para>
    /// The default is deliberately <see cref="Provenance.Inferred"/>, where <see cref="IFileParser"/>'s is
    /// <see cref="Provenance.Extracted"/>, and the asymmetry is the point: a parser CAN be language-exact,
    /// because it reads a grammar. A post-processor derives from an already-assembled graph — deriving is
    /// what inference means. Before this existed, post-processor edges bypassed the stamp entirely and an
    /// unset tier defaulted to <c>Extracted</c>, so a GUID heuristically read as an item link claimed
    /// compiler-grade trust (#400).
    /// </para>
    /// <para>
    /// A post-processor whose output really is deterministic overrides this to
    /// <see cref="Provenance.Extracted"/> — the same escape hatch parsers have. That is intentionally an
    /// explicit act: the claim has to be made, not inherited. Note the known limit: the baseline is a
    /// ceiling for the whole batch, so a post-processor emitting BOTH proven and heuristic edges would have
    /// to declare <c>Extracted</c> and would then lift the ceiling for its heuristic ones too. No current
    /// post-processor mixes; a per-producer claims model (AP1) removes the limitation.
    /// </para>
    /// Default-implemented so existing post-processors and plugins built against the older contract stay
    /// valid and binary-compatible.
    /// </summary>
    Provenance DefaultProvenance => Provenance.Inferred;

    /// <summary>Runs once over the assembled graph and returns additive enrichment + diagnostics.</summary>
    Task<GraphEnrichment> ProcessAsync(IGraphView graph);

    /// <summary>
    /// Overload that also receives host context (per-project configuration). The host invokes THIS overload.
    /// The default implementation ignores the context and forwards to <see cref="ProcessAsync(IGraphView)"/>,
    /// so plugins built against the older single-argument contract remain binary-compatible and keep working
    /// unchanged — only a post-processor that wants the context overrides this method.
    /// </summary>
    Task<GraphEnrichment> ProcessAsync(IGraphView graph, GraphPostProcessorContext context) => ProcessAsync(graph);
}
