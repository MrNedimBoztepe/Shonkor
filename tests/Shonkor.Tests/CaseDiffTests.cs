// Licensed to Shonkor under the MIT License.

extern alias bench;

using bench::Shonkor.Bench;

namespace Shonkor.Tests;

/// <summary>
/// The per-case rank-1 diff between two benchmark runs (#174).
///
/// <para>
/// The metrics are means, and a mean that moves says nothing about WHICH cases moved. When the #172 nesting
/// change shifted exact-name P@1 by 1,5 pp — inside the ±0,035 interval — "noise" was a defensible reading,
/// and so was "exactly what a systematic effect looks like at this sample size". They are indistinguishable
/// from the aggregate alone. This makes the question answerable instead of arguable.
/// </para>
/// </summary>
public class CaseDiffTests
{
    private static CaseOutcome Out(string query, string id, string type = "Method", bool matched = true) =>
        new(query, id, type, matched);

    [Fact]
    public void ACaseWhoseTop1Changed_IsReported_WithBothTheOldAndTheNewHit()
    {
        var flip = Assert.Single(CaseDiff.Compare(
            before: [Out("how are tokens hashed?", "src/Security/TokenHasher.cs::Hash")],
            after: [Out("how are tokens hashed?", "docs/security.md::Hashing", "MarkdownSection")]));

        Assert.Equal("src/Security/TokenHasher.cs::Hash", flip.Before.Top1Id);
        Assert.Equal("docs/security.md::Hashing", flip.After.Top1Id);
        // The type is carried because it is the axis #174 suspects: a MarkdownSection displacing a symbol.
        Assert.Equal("MarkdownSection", flip.After.Top1Type);
    }

    [Fact]
    public void ACaseThatDidNotMove_IsNotReported_SoTheOutputIsOnlyWhatChanged()
    {
        Assert.Empty(CaseDiff.Compare(
            before: [Out("q", "src/A.cs::A")],
            after: [Out("q", "src/A.cs::A")]));
    }

    [Fact]
    public void ACaseOnlyOneRunScored_IsIgnored_BecauseTheGoldenSetChanged_NotTheRetriever()
    {
        // Reporting these as flips would blame the retriever for an edit to the golden set — the diff would
        // cry wolf on every set change and stop being read.
        Assert.Empty(CaseDiff.Compare(
            before: [Out("only-in-before", "src/A.cs::A")],
            after: [Out("only-in-after", "src/B.cs::B")]));
    }

    [Theory]
    [InlineData(true, false, "REGRESSED")]
    [InlineData(false, true, "FIXED")]
    [InlineData(true, true, "same-verdict")]
    [InlineData(false, false, "same-verdict")]
    public void TheDirectionSeparatesARegressionFromAFix_WhichAnAggregateCannot(bool before, bool after, string expected)
    {
        // This is the point of the whole ticket: an aggregate that barely moves can hide equal numbers of
        // fixes and regressions, and only the regressions are worth acting on.
        var flip = Assert.Single(CaseDiff.Compare(
            before: [Out("q", "src/A.cs::A", matched: before)],
            after: [Out("q", "src/B.cs::B", matched: after)]));

        Assert.Equal(expected, flip.Direction);
    }

    [Fact]
    public void TheReport_PutsRegressionsFirst_SoTheOnesWorthReadingAreAtTheTop()
    {
        var flips = CaseDiff.Compare(
            before: [Out("fixed-one", "src/A.cs::A", matched: false), Out("broken-one", "src/B.cs::B", matched: true)],
            after: [Out("fixed-one", "src/A2.cs::A2", matched: true), Out("broken-one", "src/B2.cs::B2", matched: false)]);

        var report = CaseDiff.Report(flips, "hybrid");

        Assert.True(report.IndexOf("REGRESSED", StringComparison.Ordinal) < report.IndexOf("FIXED", StringComparison.Ordinal),
            $"regressions must be listed before fixes:\n{report}");
    }

    [Fact]
    public void TheReport_SaysSoExplicitlyWhenNothingMoved_RatherThanPrintingAnEmptyTable()
    {
        var report = CaseDiff.Report(CaseDiff.Compare([Out("q", "src/A.cs::A")], [Out("q", "src/A.cs::A")]), "graph");

        Assert.Contains("No case changed its rank-1 hit", report, StringComparison.Ordinal);
    }
}
