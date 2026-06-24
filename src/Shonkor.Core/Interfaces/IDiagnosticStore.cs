// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Models;

namespace Shonkor.Core.Interfaces;

/// <summary>
/// Persistence for <see cref="GraphDiagnostic"/>s produced by graph post-processors. Diagnostics are kept
/// separate from the graph and grouped by <c>source</c> (the post-processor's <see cref="IGraphPostProcessor.Name"/>),
/// so each phase-2 re-run replaces exactly its own diagnostics without touching others.
/// </summary>
public interface IDiagnosticStore
{
    /// <summary>Replaces all diagnostics for <paramref name="source"/>: clears the prior set, then inserts the new one.</summary>
    Task ReplaceDiagnosticsAsync(string source, IEnumerable<GraphDiagnostic> diagnostics, CancellationToken cancellationToken = default);

    /// <summary>Reads diagnostics, optionally filtered by minimum severity and/or code, highest severity first.</summary>
    Task<IReadOnlyList<GraphDiagnostic>> GetDiagnosticsAsync(DiagnosticSeverity? minSeverity = null, string? code = null, int maxResults = 200, CancellationToken cancellationToken = default);
}
