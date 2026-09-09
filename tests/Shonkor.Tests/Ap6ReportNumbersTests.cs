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

    /// <summary>SHA-256 digests of the class-C deny words — the same set <see cref="Ap6CorpusTests"/> embeds.</summary>
    private static readonly HashSet<string> DenyWordHashes =
    [
        "f2d758f9e379babc91f1f5062e2d486a70008cccc3c5d47b75f645e588a0ea09",
        "88578022d5b453c268a01f354193e467dbd1a8a0f95b35215ef1e2c96753119c",
        "551f67997ce2e2d23eb16079656e13eb6de0ee29619a102a6208fe566dcf14a7",
    ];

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

    [Fact]
    public void ResultsC_CarriesNoCustomerData()
    {
        // Never skips: an absent file is trivially clean; a present one is checked with the fixed patterns and the hashed deny words.
        var path = ResultsPath("C");
        if (!File.Exists(path)) return;

        var leaks = Ap6Corpus.FindLeaks(File.ReadAllText(path), DenyWordHashes);
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
