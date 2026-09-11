// Licensed to Shonkor under the MIT License.

extern alias bench;

using System.Globalization;
using System.Text.Json;
using bench::Shonkor.Bench;

namespace Shonkor.Tests;

/// <summary>
/// #473: the numbers in <c>bench/ap6-part1-report.md</c> must equal the checked-in
/// <c>bench/golden/ap6/results-&lt;class&gt;.json</c> — the same guard <see cref="ReadmeBenchmarkNumbersTests"/>
/// puts on the README, for the same reason: nothing else regenerates a report, so a re-scored run that
/// forgets one of the two files would leave them disagreeing without anyone noticing.
/// <para>
/// The per-class facts skip while no results file exists (no scored run yet); the pairing rule and the
/// leak check never skip — a results file without its report, a report without any results, or a class-C
/// results file with customer data is wrong on every checkout.
/// </para>
/// </summary>
public class Ap6ReportNumbersTests
{
    private static readonly string ReportPath = RepoPaths.File("bench", "ap6-part1-report.md");

    private static string ResultsPath(string cls) => RepoPaths.File("bench", "golden", "ap6", $"results-{cls}.json");

    /// <summary>SHA-256 digests of the class-C deny words — read from <see cref="Ap6CorpusTests.DenyWordHashes"/>, never copied.</summary>
    private static HashSet<string> DenyWordHashes => Ap6CorpusTests.DenyWordHashes;

    [Fact]
    public void ResultsAndReport_ExistTogetherOrNotAtAll()
    {
        var results = Ap6Report.Classes.Where(c => File.Exists(ResultsPath(c))).ToList();
        var report = File.Exists(ReportPath);

        Assert.True(report == results.Count > 0,
            report
                ? "bench/ap6-part1-report.md exists but no bench/golden/ap6/results-<class>.json — re-run shonkor-bench --ap6 so the numbers are pinned."
                : $"results-{string.Join('/', results)}.json exist but bench/ap6-part1-report.md does not — the report is what the gate reads; re-run shonkor-bench --ap6.");
    }

    /// <summary>
    /// Never skips: an absent file is trivially clean; a present one is checked structurally. This used to be
    /// <see cref="Ap6Corpus.FindLeaks"/> alone — fixed patterns plus hashed deny words — and #511 is the run
    /// that walked through it: three customer identifiers that matched no pattern and were on no list.
    /// <see cref="Ap6Corpus.FindResultsLeaks"/> asks the other question, the one that has no list to be
    /// incomplete: is every string in this file a token, a placeholder or vocabulary we wrote ourselves.
    /// </summary>
    [Fact]
    public void ResultsC_CarriesNoCustomerData()
    {
        var path = ResultsPath("C");
        if (!File.Exists(path)) return;

        var leaks = Ap6Corpus.FindResultsLeaks(File.ReadAllText(path), "C", DenyWordHashes);
        Assert.True(leaks.Count == 0, "results-C.json leaks customer data: " + string.Join("; ", leaks.Take(10)));

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var run in doc.RootElement.GetProperty("runs").EnumerateArray())
        {
            foreach (var f in run.GetProperty("answerFiles").EnumerateArray())
                Assert.Matches(Ap6Corpus.AnonymisedToken, f.GetString()!);
            foreach (var s in run.GetProperty("answerSymbols").EnumerateArray())
                Assert.Matches(Ap6Corpus.AnonymisedToken, s.GetString()!);
        }
    }

    [SkippableTheory]
    [InlineData("A")]
    [InlineData("B")]
    [InlineData("C")]
    public void ReportRows_MatchTheResultsFile(string cls)
    {
        Skip.IfNot(File.Exists(ResultsPath(cls)), $"results-{cls}.json not present (no scored run yet)");
        Assert.True(File.Exists(ReportPath), "results exist but the report does not");

        using var doc = JsonDocument.Parse(File.ReadAllText(ResultsPath(cls)));
        var root = doc.RootElement;
        var section = Section(File.ReadAllText(ReportPath), $"## Class {cls} —");

        // The one-way door is only shut if a reader checks it (#514): `toolCallCount` and `bashNonRg` are
        // read below under names that mean something different in v1, so a v1 file must fail here rather
        // than be compared as if the numbers were the same measurement.
        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Contains($"match mode `{root.GetProperty("matchMode").GetString()}`", section);
        foreach (var run in root.GetProperty("runs").EnumerateArray())
        {
            var task = run.GetProperty("task").GetString();
            var arm = run.GetProperty("arm").GetString();
            var n = run.GetProperty("run").GetInt32();
            var scored = run.GetProperty("scored").GetBoolean();
            var expected = string.Join(" | ",
                task, arm, n.ToString(CultureInfo.InvariantCulture),
                scored ? (run.GetProperty("correct").GetBoolean() ? "yes" : "no") : "—",
                run.GetProperty("overSelect").GetInt32().ToString(CultureInfo.InvariantCulture),
                run.GetProperty("toolCallCount").GetInt32().ToString(CultureInfo.InvariantCulture),
                run.GetProperty("tokensApprox").GetInt64().ToString(CultureInfo.InvariantCulture),
                run.GetProperty("usageExact").GetInt64().ToString(CultureInfo.InvariantCulture),
                run.GetProperty("costUsd").GetDouble().ToString("0.0000", CultureInfo.InvariantCulture),
                run.GetProperty("turns").GetInt32().ToString(CultureInfo.InvariantCulture));
            Assert.True(section.Contains($"| {expected} |", StringComparison.Ordinal),
                $"report class {cls}: no row '| {expected} |' — results-{cls}.json and bench/ap6-part1-report.md disagree; re-run shonkor-bench --ap6.");
        }

        if (cls == "C")
        {
            Assert.Equal(Ap6Scorer.GateText, root.GetProperty("gateText").GetString());
            Assert.Contains($"**Gate.** {Ap6Scorer.GateText}", section);
            Assert.Contains($"Decision: {root.GetProperty("gate").GetProperty("decision").GetString()}", section);
        }
    }

    private static string Section(string md, string heading)
    {
        var start = md.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"report: heading '{heading}' not found");
        var end = md.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);
        return end < 0 ? md[start..] : md[start..end];
    }
}
