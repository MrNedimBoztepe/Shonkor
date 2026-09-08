// Licensed to Shonkor under the MIT License.

extern alias bench;

using System.Text.Json;
using bench::Shonkor.Bench;

namespace Shonkor.Tests;

/// <summary>
/// <see cref="Ap6Report"/> (#473): one table per class, every run a row, the gate sentence verbatim, the
/// limits beside every table, the graph state per database, the open defects — and no aggregate across
/// classes anywhere. Rendered from synthetic verdicts; no run, no database.
/// </summary>
public class Ap6ReportTests
{
    private static readonly string Cwd = Path.Combine(Path.GetTempPath(), "ap6-repo");

    private static readonly Ap6Env Env = new(
        "2026-09-08T00:00:00Z", "2.1.263", "2.1.263", "ripgrep 14.1.0", "claude-test", "high", 25, 2.00, 250, 3, false,
        "<abs-path>", "abc123", "de44654380032c1766d089d859c7e3c86ac79a74", "9d7f9ce", "9d7f9ce", 0, "OK 4 plugins",
        new Dictionary<string, string> { ["ENABLE_TOOL_SEARCH"] = "false" });

    private static Ap6GraphState Graph(string name) =>
        new(name, "de44654380032c1766d089d859c7e3c86ac79a74", "fp", 5, 1000, 2000,
            new Dictionary<string, int> { ["Extracted"] = 1500, ["Inferred"] = 400, ["Ambiguous"] = 100 }, null);

    /// <summary>A, B and C with one task each: A correct in both arms, B only in mcp, C mcp-correct with a violated rg run.</summary>
    private static Ap6ReportData Data()
    {
        var tasks = new List<Ap6Task>
        {
            Ap6Fixtures.Task("A-01", "A", files: ["src/X/Foo.cs"], symbols: ["Foo"]),
            Ap6Fixtures.Task("B-01", "B", files: ["src/X/Foo.cs"], symbols: ["Foo.Bar"]),
            Ap6Fixtures.Task("C1-01", "C", files: ["Controller-03"], symbols: ["Controller-03"]),
        };
        var mapping = Ap6Fixtures.Mapping();
        var verdicts = new List<Ap6RunVerdict>();
        var calls = new Dictionary<(string, string, int), List<Ap6ToolCall>>();
        void Add(Ap6Task task, string arm, int run, string stream)
        {
            var record = Ap6RunReader.Read(stream);
            verdicts.Add(Ap6Scorer.Score(task, arm, run, record, Ap6MatchMode.Recall, Cwd, task.Class == "C" ? mapping : null));
            calls[(task.Id!, arm, run)] = record.ToolCalls;
        }
        for (var run = 1; run <= 3; run++)
        {
            Add(tasks[0], "mcp", run, Ap6Fixtures.McpStream(["src/X/Foo.cs"], ["Foo"]));
            Add(tasks[0], "rg", run, Ap6Fixtures.RgStream(["src/X/Foo.cs"], ["Foo"]));
            Add(tasks[1], "mcp", run, Ap6Fixtures.McpStream(["src/X/Foo.cs"], ["Foo.Bar", "Extra"]));
            Add(tasks[1], "rg", run, Ap6Fixtures.RgStream(["src/X/Foo.cs"], ["Foo"]));
            Add(tasks[2], "mcp", run, Ap6Fixtures.McpStream(["src/Feature/Nav/NavController.cs"], ["NavController"]));
        }
        Add(tasks[2], "rg", 1, Ap6Fixtures.RgStream(["src/Feature/Nav/NavController.cs"], ["NavController"]));
        Add(tasks[2], "rg", 2, Ap6Fixtures.Stream(
            Ap6Fixtures.Init(Ap6Fixtures.RgTools, ("shonkor", "connected")),
            Ap6Fixtures.Result(Ap6Fixtures.Answer(["src/Feature/Nav/NavController.cs"], ["NavController"]))));
        return new Ap6ReportData
        {
            RunDirName = "20260908T000000Z",
            GeneratedAt = "2026-09-08 00:00 UTC",
            MatchMode = Ap6MatchMode.Recall,
            Env = Env,
            Tasks = tasks,
            Verdicts = verdicts,
            Outcomes = Ap6Scorer.Outcomes(tasks, verdicts),
            Graphs = [Graph("Brain"), Graph("Corpus-A")],
            McpToolsOffered = Ap6Fixtures.McpTools.ToList(),
            MissingRuns = ["C1-01/rg/3"],
            Notes = ["a note"],
            ToolCalls = calls,
        };
    }

    private static string Section(string md, string heading)
    {
        var start = md.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"heading '{heading}' not found");
        var end = md.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);
        return end < 0 ? md[start..] : md[start..end];
    }

    [Fact]
    public void OneTablePerClass_EveryRunItsOwnRow()
    {
        var md = Ap6Report.Markdown(Data());

        foreach (var cls in new[] { "A", "B", "C" }) Assert.Contains($"## Class {cls} —", md);

        var a = Section(md, "## Class A");
        for (var run = 1; run <= 3; run++)
        {
            Assert.Contains($"| A-01 | mcp | {run} | yes |", a);
            Assert.Contains($"| A-01 | rg | {run} | yes |", a);
        }
        Assert.Contains("| A-01 | correct (3/3) | correct (3/3) |", a);

        var b = Section(md, "## Class B");
        Assert.Contains("| B-01 | mcp | 1 | yes | 1 |", b); // the extra symbol is overSelect, not a miss
        Assert.Contains("| B-01 | rg | 1 | no |", b);
        Assert.Contains("missingSymbols 1", b);
        Assert.Contains("| B-01 | correct (3/3) | incorrect (0/3) |", b);
    }

    [Fact]
    public void NoAggregateAcrossClasses_NoMeanNoMedian()
    {
        var md = Ap6Report.Markdown(Data());

        // The disclaimer in the header names "median"/"aggregate" to say there is none; every other line must not.
        foreach (var line in md.Split('\n'))
        {
            Assert.DoesNotContain("total", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("all classes", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("overall", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("average", line, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(1, md.Split('\n').Count(l => l.Contains("median", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void ArmViolationsAndMissingRuns_AreListedNotCounted()
    {
        var md = Ap6Report.Markdown(Data());
        var c = Section(md, "## Class C");

        Assert.Contains("| C1-01 | rg | 2 | — |", c);
        Assert.Contains("Arm violations (listed, not counted): C1-01/rg/2: MCP server(s) connected in the rg arm: shonkor", c);
        Assert.Contains("Missing runs (no stream.jsonl): C1-01/rg/3.", c);
        Assert.Contains("| C1-01 | correct (3/3) | incomplete (1/1) |", c);

        Assert.Contains("Arm violations: none.", Section(md, "## Class A"));
    }

    [Fact]
    public void ClassC_CarriesTheGateVerbatim_WithArithmeticAndDecision()
    {
        var md = Ap6Report.Markdown(Data());
        var c = Section(md, "## Class C");

        Assert.Contains("**Gate.** MCP arm correct on at least 3 more class-C tasks than the rg arm (majority of 3 runs) AND fewer file-content tokens read at equal correctness", c);
        Assert.Contains("Arithmetic: C_mcp − C_rg = 1 − 0 = 1 ≥ 3: no;", c);
        Assert.Contains("Decision: not met — stop", c);

        Assert.DoesNotContain("**Gate.**", Section(md, "## Class A"));
        Assert.DoesNotContain("**Gate.**", Section(md, "## Class B"));
    }

    [Fact]
    public void LimitsAndModel_StandBesideEveryTable()
    {
        var md = Ap6Report.Markdown(Data());
        var limits = Ap6Report.LimitsLine(Env, Ap6MatchMode.Recall);

        Assert.Contains("Model `claude-test`, effort `high`, max turns 25, max USD per run 2.00, run-set USD cap 250.00, runs per arm 3, match mode `Recall`.", limits);
        foreach (var cls in new[] { "A", "B", "C" }) Assert.Contains(limits, Section(md, $"## Class {cls}"));
    }

    [Fact]
    public void GraphState_PerDatabase_WithEdgesPerReason_AndPluginVerify()
    {
        var md = Ap6Report.Markdown(Data());
        var graphs = Section(md, "## Graph state");

        Assert.Contains("### Brain", graphs);
        Assert.Contains("### Corpus-A", graphs);
        Assert.Contains("| `de44654380032c1766d089d859c7e3c86ac79a74` | `fp` | 5 | 1000 | 2000 |", graphs);
        Assert.Contains("| Extracted | 1500 |", graphs);
        Assert.Contains("| Ambiguous | 100 |", graphs);

        var env = Section(md, "## Environment");
        Assert.Contains("| plugin verify exit | 0 |", env);
        Assert.Contains("OK 4 plugins", env);
        Assert.Contains("| shonkor.dll SHA-256 | `abc123` |", env);
        Assert.Contains("| env ENABLE_TOOL_SEARCH | `false` |", env);
    }

    [Fact]
    public void OpenDefects_AreListed_AndMcpToolsRecorded()
    {
        var md = Ap6Report.Markdown(Data());

        foreach (var issue in new[] { 429, 405, 440, 436, 463 }) Assert.Contains($"- #{issue} —", md);
        var tools = Section(md, "## MCP tools offered");
        foreach (var t in Ap6Fixtures.McpTools) Assert.Contains($"- `{t}`", tools);
        Assert.Contains("> a note", md);
    }

    [Fact]
    public void MissingEnvironment_PrintsQuestionMarks_NeverDefaults()
    {
        var data = Data();
        var md = Ap6Report.Markdown(new Ap6ReportData
        {
            RunDirName = data.RunDirName, GeneratedAt = data.GeneratedAt, MatchMode = data.MatchMode,
            Env = Ap6Env.Empty, Tasks = data.Tasks, Verdicts = data.Verdicts, Outcomes = data.Outcomes, Graphs = data.Graphs,
        });

        Assert.Contains("| model | `?` |", md);
        Assert.Contains("Model `?`, effort `?`, max turns ?, max USD per run ?, run-set USD cap ?, runs per arm ?", md);
        Assert.Contains("(not recorded)", md);
    }

    [Fact]
    public void AClassWithoutTasks_SaysSo()
    {
        var data = Data();
        var ab = data.Tasks.Where(t => t.Class != "C").ToList();
        var md = Ap6Report.Markdown(new Ap6ReportData
        {
            Env = Env, Tasks = ab, Verdicts = data.Verdicts.Where(v => v.Class != "C").ToList(),
            Outcomes = Ap6Scorer.Outcomes(ab, data.Verdicts), Graphs = [Graph("Brain")],
        });

        Assert.Contains("(not scored in this run — see notes above)", Section(md, "## Class C"));
        Assert.DoesNotContain("**Gate.**", md);
    }

    // ---------- results-<class>.json ----------

    [Fact]
    public void ResultsJson_HoldsTheRunsOfItsClassOnly_AndTheGateForC()
    {
        var data = Data();

        using var a = JsonDocument.Parse(Ap6Report.ResultsJson(data, "A"));
        var ra = a.RootElement;
        Assert.Equal("A", ra.GetProperty("class").GetString());
        Assert.Equal("Recall", ra.GetProperty("matchMode").GetString());
        Assert.Equal(6, ra.GetProperty("runs").GetArrayLength());
        Assert.All(ra.GetProperty("runs").EnumerateArray(), r => Assert.Equal("A-01", r.GetProperty("task").GetString()));
        Assert.False(ra.TryGetProperty("gate", out _));
        Assert.Equal("Brain", ra.GetProperty("graph").GetProperty("name").GetString());
        Assert.Equal("claude-test", ra.GetProperty("limits").GetProperty("model").GetString());
        Assert.Equal(1500, ra.GetProperty("graph").GetProperty("edgesByReason").GetProperty("Extracted").GetInt32());

        using var c = JsonDocument.Parse(Ap6Report.ResultsJson(data, "C"));
        var rc = c.RootElement;
        Assert.Equal(5, rc.GetProperty("runs").GetArrayLength());
        Assert.Equal(Ap6Scorer.GateText, rc.GetProperty("gateText").GetString());
        Assert.False(rc.GetProperty("gate").GetProperty("met").GetBoolean());
        Assert.Equal("Corpus-A", rc.GetProperty("graph").GetProperty("name").GetString());
        Assert.Equal(["C1-01/rg/3"], rc.GetProperty("missingRuns").EnumerateArray().Select(m => m.GetString()!).ToArray());
        var violated = rc.GetProperty("runs").EnumerateArray().Single(r => r.GetProperty("arm").GetString() == "rg" && r.GetProperty("run").GetInt32() == 2);
        Assert.False(violated.GetProperty("scored").GetBoolean());
        Assert.Contains("shonkor", violated.GetProperty("armViolation").GetString());
    }

    [Fact]
    public void ResultsJson_ForClassC_CarriesTokensNotNames_AndPassesTheLeakCheck()
    {
        var data = Data();
        var json = Ap6Report.ResultsJson(data, "C");

        Assert.Contains("\"Controller-03\"", json);
        Assert.DoesNotContain("NavController.cs", json);
        Assert.Empty(Ap6Corpus.FindLeaks(json, Ap6Fixtures.Mapping().DenyWordHashes()));
    }
}
