// Licensed to Shonkor under the MIT License.

namespace Shonkor.Core.Models;

/// <summary>Severity of a <see cref="GraphDiagnostic"/>.</summary>
public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>
/// A problem or observation a post-processor surfaces about the graph — e.g. an unresolved datasource, a
/// Helix layer violation, an ambiguous type reference. Persisted and exposed to agents/UI separately from
/// the graph (via the <c>get_diagnostics</c> MCP tool), so the graph stays clean while issues stay visible.
/// </summary>
public record GraphDiagnostic(
    string Code,                  // stable, machine-filterable, e.g. "sitecore.unresolved-datasource"
    DiagnosticSeverity Severity,
    string Message,
    string? NodeId = null,        // the node the diagnostic relates to, if any
    string? FilePath = null       // the originating file, if known
);
