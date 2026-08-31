// Licensed to Shonkor under the MIT License.

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Shonkor.Tests;

/// <summary>
/// #156: the README's benchmark tables must equal the checked-in harness output.
/// <para>
/// These numbers have gone stale <b>twice</b>, both times found by accident — once inherited unquestioned,
/// once discovered to be flat wrong on the current graph (keyword intent retrieval published as 0 %/12 %,
/// actually 9,1 %/18,2 %; token reduction published as 85,7 %, actually 75,9 %). The cause was structural:
/// nothing regenerated them, so every graph-shaping change silently invalidated the landing page.
/// </para>
/// <para>
/// This test closes that hole. It parses the numbers straight out of <c>README.md</c> and asserts they match
/// <c>bench/metrics-*.json</c>. Comparing metrics-to-metrics would only guard <i>regressions</i>; the failure
/// mode we actually keep hitting is <b>documentation drift</b>, so the README itself has to be the input.
/// </para>
/// </summary>
public class ReadmeBenchmarkNumbersTests
{
    /// <summary>Rounding slack: the README prints one decimal, so anything under half a tenth is formatting.</summary>
    private const double Tolerance = 0.05;

    private static string Readme() => File.ReadAllText(RepoPaths.File("README.md"));

    private static JsonElement Metrics(string file) =>
        JsonDocument.Parse(File.ReadAllText(RepoPaths.File("bench", file))).RootElement;

    /// <summary>
    /// "89,0 %" / "**94,5 %**" / "481.539" → a double. The README uses German separators (comma = decimal,
    /// dot = thousands) and bolds the winning row, so the markers have to come off before parsing.
    /// </summary>
    private static double Num(string raw) => double.Parse(
        raw.Replace("%", "").Replace("*", "").Replace(".", "").Replace(",", ".").Trim(),
        CultureInfo.InvariantCulture);

    /// <summary>Percent cells of the README row whose first cell starts with <paramref name="label"/>, inside <paramref name="section"/>.</summary>
    private static double[] Row(string section, string label)
    {
        foreach (var line in section.Split('\n'))
        {
            var cells = line.Split('|', StringSplitOptions.TrimEntries);
            // A markdown row splits to ["", c1, c2, ..., ""] — cells[1] is the label column.
            if (cells.Length < 4) continue;
            if (!cells[1].Replace("*", "").StartsWith(label, StringComparison.OrdinalIgnoreCase)) continue;
            return cells.Skip(2).Where(c => c.Contains('%')).Select(Num).ToArray();
        }
        throw new InvalidOperationException($"README: no table row labelled '{label}' in the expected section.");
    }

    /// <summary>The slice of the README between two headings — so the two retrieval tables can't be confused.</summary>
    private static string Section(string readme, string startHeading, string endHeading)
    {
        var start = readme.IndexOf(startHeading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"README: heading '{startHeading}' not found — did the numbers section get restructured?");
        var end = readme.IndexOf(endHeading, start, StringComparison.Ordinal);
        return end < 0 ? readme[start..] : readme[start..end];
    }

    public static TheoryData<string, string, string> RetrievalRows() => new()
    {
        // README label            metrics file                    retriever key
        { "Keyword",               "metrics-exactname.json",       "graph" },
        { "Vector only",           "metrics-exactname.json",       "semantic" },
        { "Hybrid",                "metrics-exactname.json",       "hybrid" },
        { "Keyword",               "metrics-agent-queries.json",   "graph" },
        { "Vector only",           "metrics-agent-queries.json",   "semantic" },
        { "Hybrid",                "metrics-agent-queries.json",   "hybrid" },
    };

    [Theory]
    [MemberData(nameof(RetrievalRows))]
    public void RetrievalTable_MatchesCheckedInMetrics(string label, string metricsFile, string retriever)
    {
        var readme = Readme();
        var exactName = metricsFile.Contains("exactname");

        // The two tables live in the same section; split them on the sub-headline between them.
        var section = exactName
            ? Section(readme, "**You already know the name**", "**You describe what you mean**")
            : Section(readme, "**You describe what you mean**", "### 2.");

        var cells = Row(section, label);
        Assert.True(cells.Length == 2, $"README row '{label}' should have 2 percent cells, found {cells.Length}.");

        var m = Metrics(metricsFile).GetProperty("retrieval").GetProperty(retriever);
        var p1 = m.GetProperty("precisionAt1").GetDouble() * 100;
        var recall = m.GetProperty("recallAtK").GetDouble() * 100;

        Assert.True(Math.Abs(cells[0] - p1) < Tolerance,
            $"README '{label}' top-hit ({cells[0]:F1} %) != {metricsFile}:{retriever}.precisionAt1 ({p1:F1} %). " +
            "Re-run the harness and update the README, or the landing page is lying again.");
        Assert.True(Math.Abs(cells[1] - recall) < Tolerance,
            $"README '{label}' top-10 ({cells[1]:F1} %) != {metricsFile}:{retriever}.recallAtK ({recall:F1} %).");
    }

    [Fact]
    public void TokenReduction_MatchesCheckedInMetrics()
    {
        // "**481.539 → 115.978 tokens across 7 queries — 75,9 % fewer.**"
        var match = Regex.Match(Readme(), @"\*\*([\d.]+) → ([\d.]+) tokens across (\d+) queries — ([\d,]+) % fewer");
        Assert.True(match.Success, "README: the token-reduction sentence is missing or reworded — update this guard with it.");

        var metrics = Metrics("metrics-exactname.json");
        var expectedPct = metrics.GetProperty("tokenReductionPct").GetDouble();
        var expectedQueries = metrics.GetProperty("tokenQueries").GetInt32();

        Assert.Equal(expectedQueries, int.Parse(match.Groups[3].Value));
        Assert.True(Math.Abs(Num(match.Groups[4].Value) - expectedPct) < Tolerance,
            $"README token reduction ({match.Groups[4].Value} %) != metrics-exactname.json ({expectedPct:F1} %).");

        // The before/after token counts must agree with the percentage they claim to produce.
        double before = Num(match.Groups[1].Value), after = Num(match.Groups[2].Value);
        var impliedPct = (1 - after / before) * 100;
        Assert.True(Math.Abs(impliedPct - expectedPct) < 0.2,
            $"README token counts ({before:N0} → {after:N0}) imply {impliedPct:F1} %, but it claims {expectedPct:F1} %.");
    }

    [Fact]
    public void RagHeadToHead_2x2_MatchesCheckedInMetrics()
    {
        // All FOUR cells of the 2×2 are guarded (#166). Each row has two percent cells: the no-graph column
        // and the graph column. The unflattering cells — the vector-only diagonal where Shonkor does NOT win,
        // and the baseline-hybrid cell that shows the keyword arm added nothing — get exactly the same
        // protection as the +9,1 the page leads with. That symmetry is the whole point of the guard.
        var section = Section(Readme(), "### 3.", "## ✨");
        var m = Metrics("metrics-agent-queries.json").GetProperty("ragBaseline");
        Assert.True(m.ValueKind != JsonValueKind.Null,
            "bench/metrics-agent-queries.json has no ragBaseline — re-run the set with --compare-rag.");

        // (row label, [no-graph cell key, graph cell key]) — the 2×2, read left-to-right per row.
        (string Label, string NoGraphKey, string GraphKey)[] rows =
        [
            ("vector-only seeds", "ragCoverage",       "shonkorCoverage"),
            ("hybrid seeds",      "ragHybridCoverage", "shonkorHybridCoverage"),
        ];

        foreach (var (label, noGraphKey, graphKey) in rows)
        {
            var cells = Row(section, label);
            Assert.True(cells.Length == 2, $"README 2×2 row '{label}' should have 2 percent cells, found {cells.Length}.");

            var noGraph = m.GetProperty(noGraphKey).GetDouble() * 100;
            var graph = m.GetProperty(graphKey).GetDouble() * 100;
            Assert.True(Math.Abs(cells[0] - noGraph) < Tolerance,
                $"README 2×2 '{label}' no-graph cell ({cells[0]:F1} %) != {noGraphKey} ({noGraph:F1} %).");
            Assert.True(Math.Abs(cells[1] - graph) < Tolerance,
                $"README 2×2 '{label}' graph cell ({cells[1]:F1} %) != {graphKey} ({graph:F1} %).");
        }
    }

    [Fact]
    public void RagHeadToHead_PublishesTheHonestyCaveat()
    {
        // The page leads with "+9,1 pp graph contribution". That number is only honest next to the caveat
        // that the baseline's keyword arm barely fired (so part of the gain is the graph's richer indexed
        // unit, not pure topology). If a future edit keeps the flattering number and drops the caveat, this
        // fails — banking the win while hiding the confound is exactly what #166 exists to prevent.
        var section = Section(Readme(), "### 3.", "## ✨");
        var fired = Metrics("metrics-agent-queries.json").GetProperty("ragBaseline")
            .GetProperty("ragKeywordFiredQueries").GetInt32();

        Assert.Contains($"{fired} of 33", section);
        Assert.Contains("indexed unit", section); // the confound must be named, not just numbered
    }

    [Fact]
    public void RagHeadToHead_PublishesTheSummaryDecomposition()
    {
        // #189: the caveat above *names* a confound — the +9,1 might be the AI summaries, not the topology.
        // Naming it is not enough; a sharp customer asks which, so we measured it (`--enrich-baseline`) and
        // must publish the answer next to the claim. This guards that the reproducible command and the
        // interval-not-point framing stay on the page — dropping the finding to leave only the flattering
        // number is the exact #166 failure mode this file exists to catch, one decomposition deeper.
        var section = Section(Readme(), "### 3.", "## ✨");

        // The reproducible command a reader runs to check us — the finding is worthless if it can't be re-run.
        Assert.Contains("--enrich-baseline", section);
        // Reported against the confidence interval, never as a point estimate (the #284 discipline).
        Assert.Contains("noise band", section);
    }
}
