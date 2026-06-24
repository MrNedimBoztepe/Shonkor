// Licensed to Shonkor under the MIT License.

using System;
using System.Collections.Generic;

namespace Shonkor.Core.Models;

/// <summary>
/// The additive output of an <see cref="Shonkor.Core.Interfaces.IGraphPostProcessor"/>: extra graph nodes
/// and edges to merge, plus diagnostics to record. Phase-2 output is tagged by the host so it can be
/// replaced wholesale on re-index without touching phase-1 (per-file) data.
/// </summary>
public record GraphEnrichment(
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges,
    IReadOnlyList<GraphDiagnostic> Diagnostics)
{
    public static GraphEnrichment Empty { get; } =
        new(Array.Empty<GraphNode>(), Array.Empty<GraphEdge>(), Array.Empty<GraphDiagnostic>());
}
