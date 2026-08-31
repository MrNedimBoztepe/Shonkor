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

/// <summary>
/// Shared factory for the diagnostics a post-processor emits *about itself* rather than about the graph.
/// Lives in Core so the scanner, the first-party processors and plugin-supplied ones all produce the very
/// same code — a marker an agent has to recognise is worthless if every producer spells it differently.
/// </summary>
public static class PostProcessorDiagnostics
{
    /// <summary>Stable code for "this post-processor did not finish, so its findings are incomplete".</summary>
    public const string IncompleteCode = "postprocessor.incomplete";

    /// <summary>
    /// The marker that makes an incomplete pass visible as data (#353). A post-processor that fails used to
    /// leave nothing but a log line — on the CLI path a stderr warning, in the web UI nothing at all — so a
    /// graph with no <c>security.*</c> diagnostics was indistinguishable from one where the check never
    /// produced a result. With this marker "no findings" means *checked and clean*.
    ///
    /// <para>
    /// <b>Severity is <see cref="DiagnosticSeverity.Error"/> deliberately</b>: <c>GetDiagnosticsAsync</c>
    /// orders by severity and caps the result (default 200) and the MCP/stats callers pass no limit, so a
    /// Warning-level marker could drop out of the list behind a few hundred warnings. A fail-open signal that
    /// can itself disappear is no signal.
    /// </para>
    ///
    /// <para>
    /// It is deliberately unanchored (<c>NodeId</c>/<c>FilePath</c> stay <c>null</c>): the incompleteness is a
    /// property of the whole pass, and pinning it to an arbitrary node would read as a finding about that node.
    /// The processor name goes into the <c>Message</c> because the stored <c>Source</c> column is not part of
    /// the query projection — without it the reader could not tell which check came up short.
    /// </para>
    /// </summary>
    /// <param name="processorName">The <c>IGraphPostProcessor.Name</c> of the pass that did not complete.</param>
    /// <param name="reason">Short, human-readable cause (exception message, number of skipped nodes, …).</param>
    public static GraphDiagnostic Incomplete(string processorName, string reason) =>
        new(
            Code: IncompleteCode,
            Severity: DiagnosticSeverity.Error,
            Message: $"Post-processor '{processorName}' did not complete ({reason}). Its results for this scan are " +
                     "incomplete — the absence of findings from this check does not mean the graph is clean. " +
                     "Re-index to get a full pass.");
}
