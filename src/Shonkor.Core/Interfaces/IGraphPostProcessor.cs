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
