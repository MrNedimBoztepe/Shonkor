// Licensed to Shonkor under the MIT License.

namespace Shonkor.Core.Services;

/// <summary>
/// Relationships that are asserted <b>about</b> code rather than extracted <b>from</b> it, and therefore
/// must survive a re-index of the file they are anchored to.
///
/// <para>
/// Measured on a real graph (#434): a full reparse of <c>sitecoreMuM</c> took <c>RELATES_TO</c> from
/// 28 145 edges to 1 061 — 27 084 destroyed, because the clearing pass drops every edge on a reparsed
/// file's nodes and nothing in the scan writes these back. Their only producer is the LLM enrichment
/// pass, and re-running it does not recover them: asked twice on byte-identical input, the generator
/// reproduced its own answer for 8 of 48 nodes.
/// </para>
///
/// <para>
/// So the rule is not "these edges are more important". It is that a scan can rebuild what it extracted
/// and cannot rebuild what someone else asserted, and deleting the second kind on the strength of having
/// re-read the first is a category error. <c>INFLUENCES</c> and <c>AFFECTS</c> are listed for
/// completeness — their endpoints carry no <c>FilePath</c>, so no clearing pass reaches them today, and
/// that is an accident of the record tool's id scheme rather than a guarantee worth relying on.
/// </para>
/// </summary>
public static class AgentAuthoredRelations
{
    /// <summary>The relationship kinds that outlive a re-index of their anchor.</summary>
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "RELATES_TO",
        "INFLUENCES",
        "AFFECTS",
    };

    /// <summary>Whether a relationship must be preserved when its anchor file is cleared for re-indexing.</summary>
    public static bool SurvivesReindex(string? relationship) =>
        relationship is not null && All.Contains(relationship);

    /// <summary>
    /// A SQL fragment excluding these relationships from a <c>DELETE FROM Edges</c>, as a literal list —
    /// the set is a closed, code-owned constant, so there is no user input to parameterize.
    /// </summary>
    public static string SqlExclusion { get; } =
        "RelationType NOT IN (" + string.Join(", ", All.Order(StringComparer.Ordinal).Select(r => $"'{r}'")) + ")";
}
