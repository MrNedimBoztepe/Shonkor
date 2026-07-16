// Licensed to Shonkor under the MIT License.

using System.Text;

namespace Shonkor.Bench;

/// <summary>One case whose rank-1 hit differs between two runs (#174).</summary>
/// <param name="Query">The golden case, joined by its query text.</param>
/// <param name="Before">The rank-1 outcome in the baseline run.</param>
/// <param name="After">The rank-1 outcome in the current run.</param>
internal sealed record CaseFlip(string Query, CaseOutcome Before, CaseOutcome After)
{
    /// <summary>
    /// Which way this case moved. The direction is the whole point: an aggregate that barely moves can hide
    /// equal numbers of fixes and regressions, and only the regressions are worth acting on.
    /// </summary>
    public string Direction => (Before.Matched, After.Matched) switch
    {
        (true, false) => "REGRESSED",
        (false, true) => "FIXED",
        _ => "same-verdict"   // the top hit changed, but both/neither matched — usually a re-ranking within
                              // equally-correct nodes, and the cheapest class to dismiss once seen.
    };
}

/// <summary>
/// Compares the per-case records of two benchmark runs (#174).
///
/// <para>
/// The metrics are means. When the #172 nesting change moved exact-name P@1 by 1,5 pp, "noise" and "a
/// systematic effect at this sample size" were <i>indistinguishable from the aggregate alone</i> — and
/// calling it noise because CI permits it is the reasoning that let the benchmark report nothing at all for
/// months (#157). This turns that argument into a command: diff two runs, read the cases that flipped.
/// </para>
/// <para>
/// Pure by design (no I/O, no DB) so the comparison itself is unit-testable — the harness's own logic is
/// exactly what #191 found untested.
/// </para>
/// </summary>
internal static class CaseDiff
{
    /// <summary>
    /// Cases present in BOTH runs whose rank-1 hit changed. Cases only one run scored are ignored: the
    /// golden set changed under us, and reporting them as flips would blame the retriever for an edit.
    /// </summary>
    public static List<CaseFlip> Compare(IEnumerable<CaseOutcome> before, IEnumerable<CaseOutcome> after)
    {
        var baseline = before
            .GroupBy(o => o.Query, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        return after
            .Where(a => baseline.ContainsKey(a.Query))
            .Select(a => (Before: baseline[a.Query], After: a))
            .Where(p => !string.Equals(p.Before.Top1Id, p.After.Top1Id, StringComparison.Ordinal))
            .Select(p => new CaseFlip(p.After.Query, p.Before, p.After))
            .ToList();
    }

    /// <summary>Renders the flips, regressions first — the ones worth reading are at the top.</summary>
    public static string Report(IReadOnlyList<CaseFlip> flips, string retriever)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## {retriever}");

        if (flips.Count == 0)
        {
            sb.AppendLine("No case changed its rank-1 hit.");
            return sb.ToString();
        }

        var order = new Dictionary<string, int> { ["REGRESSED"] = 0, ["FIXED"] = 1, ["same-verdict"] = 2 };
        sb.AppendLine($"{flips.Count} case(s) changed their rank-1 hit.");
        sb.AppendLine();
        sb.AppendLine("| | Query | Before | After |");
        sb.AppendLine("|---|---|---|---|");

        foreach (var f in flips.OrderBy(f => order[f.Direction]).ThenBy(f => f.Query, StringComparer.Ordinal))
        {
            sb.AppendLine($"| {f.Direction} | {f.Query} | `{f.Before.Top1Id}` ({f.Before.Top1Type}) | `{f.After.Top1Id}` ({f.After.Top1Type}) |");
        }

        return sb.ToString();
    }
}
