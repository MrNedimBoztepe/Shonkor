// Licensed to Shonkor under the MIT License.

extern alias bench;

using bench::Shonkor.Bench;

namespace Shonkor.Tests;

/// <summary>
/// <see cref="Ap6Scorer"/> (#473): normalisation of what an arm wrote, the arm check read from
/// <c>system/init</c>, Recall vs Exact, <c>noAnswer</c>, the majority over three runs and the class-C gate.
/// All over synthetic streams — no run, no database.
/// </summary>
public class Ap6ScorerTests
{
    /// <summary>An absolute repository root that is rooted on the running platform (drive letter or <c>/</c>).</summary>
    private static readonly string Cwd = Path.Combine(Path.GetTempPath(), "ap6-repo");

    private static Ap6RunVerdict ScoreMcp(Ap6Task task, string[] files, string[] symbols, Ap6MatchMode mode = Ap6MatchMode.Recall, int run = 1, int version = 1) =>
        Ap6Scorer.Score(task, Ap6Scorer.McpArm, run, Ap6RunReader.Read(Ap6Fixtures.McpStream(files, symbols, version: version)), mode, Cwd, null);

    private static Ap6RunVerdict ScoreRg(Ap6Task task, string[] files, string[] symbols, Ap6MatchMode mode = Ap6MatchMode.Recall, int run = 1) =>
        Ap6Scorer.Score(task, Ap6Scorer.RgArm, run, Ap6RunReader.Read(Ap6Fixtures.RgStream(files, symbols)), mode, Cwd, null);

    // ---------- normalisation ----------

    [Theory]
    [InlineData("src/A/B.cs", "src/A/B.cs")]
    [InlineData("`src/A/B.cs`", "src/A/B.cs")]
    [InlineData("\"src\\A\\B.cs\"", "src/A/B.cs")]
    [InlineData("./src/A/B.cs", "src/A/B.cs")]
    [InlineData("@/src/A/B.cs", "src/A/B.cs")]
    [InlineData("@/src/A/B.cs::Ns.Type::Member", "src/A/B.cs")]
    [InlineData("src/A/B.cs:12", "src/A/B.cs")]
    [InlineData("src/A/B.cs:12-40", "src/A/B.cs")]
    [InlineData("  src/A/B.cs  ", "src/A/B.cs")]
    public void NormalizePath_StripsDecorationsAndUsesForwardSlashes(string raw, string expected)
    {
        Assert.Equal(expected, Ap6Scorer.NormalizePath(raw, Cwd));
    }

    [Fact]
    public void NormalizePath_RootRelativeSlash_IsAnMsysArtifactOnWindows_AndAnAbsolutePathOnPosix()
    {
        // The same string means two different things: on Windows "/src/A/B.cs" cannot be a real absolute file
        // path, so it is read as repository-relative; on POSIX it is a genuine absolute path outside cwd and
        // must not be silently turned into a relative one (that is how the Linux leg first caught this).
        var expected = OperatingSystem.IsWindows() ? "src/A/B.cs" : "/src/A/B.cs";
        Assert.Equal(expected, Ap6Scorer.NormalizePath("/src/A/B.cs", Cwd));
    }

    [Fact]
    public void NormalizePath_RelativisesAnAbsolutePathUnderTheWorkingDirectory()
    {
        var absolute = Path.Combine(Cwd, "src", "A", "B.cs");
        Assert.Equal("src/A/B.cs", Ap6Scorer.NormalizePath(absolute, Cwd));
        Assert.Equal("src/A/B.cs", Ap6Scorer.NormalizePath(absolute.Replace('\\', '/') + ":7", Cwd));
    }

    [Fact]
    public void NormalizePath_LeavesAnAbsolutePathOutsideTheWorkingDirectoryAlone()
    {
        var outside = Path.Combine(Path.GetTempPath(), "other-repo", "A.cs").Replace('\\', '/');
        Assert.Equal(outside, Ap6Scorer.NormalizePath(outside, Cwd));
    }

    [Theory]
    [InlineData("Foo", "Foo")]
    [InlineData("`Foo.Bar`", "Foo.Bar")]
    [InlineData("Ns.Sub.Foo", "Ns.Sub.Foo")]
    [InlineData("Foo.Bar(int, string)", "Foo.Bar")]
    [InlineData("Foo<T>.Bar<U>", "Foo.Bar")]
    [InlineData("Foo`1.Bar", "Foo.Bar")]
    [InlineData("Foo.Bar#2", "Foo.Bar")]
    [InlineData("Foo::Bar", "Foo.Bar")]
    [InlineData("@/src/A.cs::Ns.Sub.Foo::Bar#1", "Foo.Bar")]
    [InlineData("@/src/A.cs::Ns.Foo", "Foo")]
    [InlineData("@/src/A.cs::Ns.Foo`1::Bar(int)", "Foo.Bar")]
    [InlineData("List<Dictionary<string, int>>.Add", "List.Add")]
    public void NormalizeSymbol_CollapsesHandlesAndDropsSignatures(string raw, string expected)
    {
        Assert.Equal(expected, Ap6Scorer.NormalizeSymbol(raw));
    }

    [Theory]
    [InlineData("Foo", "Foo", true)]
    [InlineData("Ns.Foo", "Foo", true)]
    [InlineData("Ns.Foo.Bar", "Foo.Bar", true)]
    [InlineData("Foo.Bar", "Foo", false)]      // a member does not name its type
    [InlineData("Foo", "Foo.Bar", false)]      // a type does not name its member
    [InlineData("OtherFoo", "Foo", false)]
    [InlineData("foo", "Foo", false)]          // identifiers are case-sensitive
    public void SymbolMatches_AlignsDottedSegmentsFromTheRight(string answer, string key, bool expected)
    {
        Assert.Equal(expected, Ap6Scorer.SymbolMatches(answer, key));
    }

    // ---------- the arm ----------

    [Fact]
    public void McpArm_IsValid_WhenOnlyShonkorToolsAndShonkorConnected()
    {
        var r = Ap6RunReader.Read(Ap6Fixtures.Stream(Ap6Fixtures.Init(Ap6Fixtures.McpTools, ("shonkor", "connected"))));
        Assert.Null(Ap6Scorer.ArmViolation(r, Ap6Scorer.McpArm));
    }

    [Theory]
    [InlineData("Bash")]
    [InlineData("Read")]
    [InlineData("Grep")]
    public void McpArm_IsViolated_ByABuiltInTool(string tool)
    {
        var r = Ap6RunReader.Read(Ap6Fixtures.Stream(Ap6Fixtures.Init(Ap6Fixtures.McpTools.Append(tool), ("shonkor", "connected"))));
        var v = Ap6Scorer.ArmViolation(r, Ap6Scorer.McpArm);
        Assert.NotNull(v);
        Assert.Contains(tool, v);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("failed")]
    public void McpArm_IsViolated_WhenShonkorIsNotConnected(string status)
    {
        var r = Ap6RunReader.Read(Ap6Fixtures.Stream(Ap6Fixtures.Init(Ap6Fixtures.McpTools, ("shonkor", status))));
        var v = Ap6Scorer.ArmViolation(r, Ap6Scorer.McpArm);
        Assert.NotNull(v);
        Assert.Contains(status, v);
    }

    [Fact]
    public void McpArm_IsViolated_WhenNoShonkorToolIsListed_OrAnotherServerIsConnected()
    {
        var none = Ap6RunReader.Read(Ap6Fixtures.Stream(Ap6Fixtures.Init([], ("shonkor", "connected"))));
        Assert.NotNull(Ap6Scorer.ArmViolation(none, Ap6Scorer.McpArm));

        var other = Ap6RunReader.Read(Ap6Fixtures.Stream(Ap6Fixtures.Init(Ap6Fixtures.McpTools, ("shonkor", "connected"), ("filesystem", "connected"))));
        var v = Ap6Scorer.ArmViolation(other, Ap6Scorer.McpArm);
        Assert.NotNull(v);
        Assert.Contains("filesystem", v);
    }

    [Fact]
    public void RgArm_IsValid_WithBashAndReadAndNoServer()
    {
        var r = Ap6RunReader.Read(Ap6Fixtures.Stream(Ap6Fixtures.Init(Ap6Fixtures.RgTools)));
        Assert.Null(Ap6Scorer.ArmViolation(r, Ap6Scorer.RgArm));

        // A server that is listed but not connected is not a violation of the rg arm.
        var failed = Ap6RunReader.Read(Ap6Fixtures.Stream(Ap6Fixtures.Init(Ap6Fixtures.RgTools, ("shonkor", "failed"))));
        Assert.Null(Ap6Scorer.ArmViolation(failed, Ap6Scorer.RgArm));
    }

    [Fact]
    public void RgArm_IsViolated_ByAConnectedServer_AnExtraTool_OrAMissingOne()
    {
        var server = Ap6RunReader.Read(Ap6Fixtures.Stream(Ap6Fixtures.Init(Ap6Fixtures.RgTools, ("shonkor", "connected"))));
        Assert.Contains("shonkor", Ap6Scorer.ArmViolation(server, Ap6Scorer.RgArm)!);

        var extra = Ap6RunReader.Read(Ap6Fixtures.Stream(Ap6Fixtures.Init(Ap6Fixtures.RgTools.Append("mcp__shonkor__locate"))));
        Assert.Contains("mcp__shonkor__locate", Ap6Scorer.ArmViolation(extra, Ap6Scorer.RgArm)!);

        var grep = Ap6RunReader.Read(Ap6Fixtures.Stream(Ap6Fixtures.Init(Ap6Fixtures.RgTools.Append("Grep"))));
        Assert.Contains("Grep", Ap6Scorer.ArmViolation(grep, Ap6Scorer.RgArm)!);

        var missing = Ap6RunReader.Read(Ap6Fixtures.Stream(Ap6Fixtures.Init(["Bash"])));
        Assert.Contains("Read", Ap6Scorer.ArmViolation(missing, Ap6Scorer.RgArm)!);
    }

    [Fact]
    public void BothArms_AreViolated_WithoutAnInitEvent()
    {
        var r = Ap6RunReader.Read(Ap6Fixtures.Stream(Ap6Fixtures.Result(Ap6Fixtures.Answer([], []))));
        Assert.NotNull(Ap6Scorer.ArmViolation(r, Ap6Scorer.McpArm));
        Assert.NotNull(Ap6Scorer.ArmViolation(r, Ap6Scorer.RgArm));
    }

    // ---------- one run ----------

    [Fact]
    public void Recall_IsCorrectWithExtras_AndCountsThemAsOverSelect()
    {
        var task = Ap6Fixtures.Task("B-01", "B", files: ["src/X/Foo.cs"], symbols: ["Foo.Bar"]);

        var v = ScoreMcp(task, ["src/X/Foo.cs", "src/X/Other.cs"], ["Ns.Foo.Bar", "Baz"]);

        Assert.True(v.Scored);
        Assert.True(v.Correct);
        Assert.Equal(2, v.OverSelect);
        Assert.Equal(0, v.MissingFiles);
        Assert.Equal(0, v.MissingSymbols);
        Assert.False(v.NoAnswer);
    }

    [Fact]
    public void Exact_IsNotCorrectWithExtras_ButIsWithTheKeyOnly()
    {
        var task = Ap6Fixtures.Task("B-01", "B", files: ["src/X/Foo.cs"], symbols: ["Foo.Bar"]);

        var extras = ScoreMcp(task, ["src/X/Foo.cs", "src/X/Other.cs"], ["Foo.Bar"], Ap6MatchMode.Exact);
        Assert.False(extras.Correct);
        Assert.Equal(1, extras.OverSelect);

        var exact = ScoreMcp(task, ["src/X/Foo.cs"], ["Foo.Bar"], Ap6MatchMode.Exact);
        Assert.True(exact.Correct);
    }

    [Fact]
    public void AMissingKeyFileOrSymbol_IsIncorrectInBothModes()
    {
        var task = Ap6Fixtures.Task("B-01", "B", files: ["src/X/Foo.cs", "src/X/Bar.cs"], symbols: ["Foo"]);

        var missingFile = ScoreMcp(task, ["src/X/Foo.cs"], ["Foo"]);
        Assert.False(missingFile.Correct);
        Assert.Equal(1, missingFile.MissingFiles);

        var missingSymbol = ScoreMcp(task, ["src/X/Foo.cs", "src/X/Bar.cs"], ["Bar"], Ap6MatchMode.Exact);
        Assert.False(missingSymbol.Correct);
        Assert.Equal(1, missingSymbol.MissingSymbols);
    }

    [Fact]
    public void Paths_CompareByThePlatformRule_SymbolsOrdinally()
    {
        var task = Ap6Fixtures.Task("A-01", "A", files: ["src/X/Foo.cs"], symbols: ["Foo"]);

        var casing = ScoreRg(task, ["SRC/x/foo.CS"], ["Foo"]);
        Assert.Equal(OperatingSystem.IsWindows(), casing.Correct);

        var symbolCasing = ScoreRg(task, ["src/X/Foo.cs"], ["foo"]);
        Assert.False(symbolCasing.Correct);
    }

    [Fact]
    public void NoAnswer_IsIncorrectAndFlagged()
    {
        var task = Ap6Fixtures.Task("A-01", "A");
        var stream = Ap6Fixtures.Stream(Ap6Fixtures.Init(Ap6Fixtures.RgTools), Ap6Fixtures.Result(null, isError: true));

        var v = Ap6Scorer.Score(task, Ap6Scorer.RgArm, 1, Ap6RunReader.Read(stream), Ap6MatchMode.Recall, Cwd, null);

        Assert.True(v.Scored);
        Assert.True(v.NoAnswer);
        Assert.False(v.Correct);
        Assert.True(v.IsError);
    }

    [Fact]
    public void AWrongSchemaVersion_IsNoAnswer_EvenWhenTheContentIsRight()
    {
        var task = Ap6Fixtures.Task("A-01", "A", files: ["src/X/Foo.cs"], symbols: ["Foo"]);

        var v = ScoreMcp(task, ["src/X/Foo.cs"], ["Foo"], version: 2);

        Assert.True(v.NoAnswer);
        Assert.False(v.Correct);
    }

    [Fact]
    public void AnArmViolation_IsListedNotScored_InBothArms()
    {
        var task = Ap6Fixtures.Task("A-01", "A", files: ["src/X/Foo.cs"], symbols: ["Foo"]);

        // The MCP arm with Bash available — the right answer does not count.
        var mcp = Ap6Fixtures.Stream(
            Ap6Fixtures.Init(Ap6Fixtures.McpTools.Append("Bash"), ("shonkor", "connected")),
            Ap6Fixtures.Result(Ap6Fixtures.Answer(["src/X/Foo.cs"], ["Foo"])));
        var vm = Ap6Scorer.Score(task, Ap6Scorer.McpArm, 1, Ap6RunReader.Read(mcp), Ap6MatchMode.Recall, Cwd, null);
        Assert.False(vm.Scored);
        Assert.False(vm.Correct);
        Assert.Contains("Bash", vm.ArmViolation!);

        // The rg arm with shonkor connected — likewise.
        var rg = Ap6Fixtures.Stream(
            Ap6Fixtures.Init(Ap6Fixtures.RgTools, ("shonkor", "connected")),
            Ap6Fixtures.Result(Ap6Fixtures.Answer(["src/X/Foo.cs"], ["Foo"])));
        var vr = Ap6Scorer.Score(task, Ap6Scorer.RgArm, 1, Ap6RunReader.Read(rg), Ap6MatchMode.Recall, Cwd, null);
        Assert.False(vr.Scored);
        Assert.False(vr.Correct);
        Assert.Contains("shonkor", vr.ArmViolation!);
    }

    [Fact]
    public void TheVerdict_CarriesTheRunCounters()
    {
        var task = Ap6Fixtures.Task("A-01", "A", files: ["src/X/Foo.cs"], symbols: ["Foo"]);
        var stream = Ap6Fixtures.Stream(
            Ap6Fixtures.Init(Ap6Fixtures.RgTools),
            Ap6Fixtures.ToolUse("Bash", new { command = "rg -n Foo src" }, "m1"),
            Ap6Fixtures.ToolResultString(new string('x', 400)),
            Ap6Fixtures.ToolUse("Bash", new { command = "ls" }, "m2"),
            Ap6Fixtures.ToolResultString(new string('y', 40)),
            Ap6Fixtures.Result(Ap6Fixtures.Answer(["src/X/Foo.cs"], ["Foo"]), cost: 0.42, turns: 5));

        var v = Ap6Scorer.Score(task, Ap6Scorer.RgArm, 2, Ap6RunReader.Read(stream), Ap6MatchMode.Recall, Cwd, null);

        Assert.Equal(2, v.Run);
        Assert.Equal(2, v.ToolCalls);
        Assert.Equal(110, v.TokensApprox);
        Assert.Equal(240, v.UsageExact);
        Assert.Equal(0.42, v.CostUsd, 6);
        Assert.Equal(5, v.Turns);
        Assert.Equal(1, v.BashNonRg);
        Assert.Equal(["src/X/Foo.cs"], v.AnswerFiles);
        Assert.Equal(["Foo"], v.AnswerSymbols);
    }

    [Fact]
    public void ClassC_WithoutAMapping_CarriesNothingAndCannotBeCorrect()
    {
        var task = Ap6Fixtures.Task("C1-01", "C", files: ["Controller-01"], symbols: ["Controller-01"]);

        var v = ScoreMcp(task, ["src/Feature/Hero/HeroController.cs"], ["HeroController"]);

        Assert.False(v.Correct);
        Assert.Empty(v.AnswerFiles);
        Assert.Empty(v.AnswerSymbols);
    }

    [Fact]
    public void ClassC_WithTheMapping_IsScoredOnTokens()
    {
        var task = Ap6Fixtures.Task("C1-01", "C", files: ["Controller-03"], symbols: ["Controller-03"]);
        var record = Ap6RunReader.Read(Ap6Fixtures.McpStream(["src/Feature/Nav/NavController.cs"], ["Acme.Feature.Nav.NavController"]));

        var v = Ap6Scorer.Score(task, Ap6Scorer.McpArm, 1, record, Ap6MatchMode.Exact, Cwd, Ap6Fixtures.Mapping());

        Assert.True(v.Correct);
        Assert.Equal(["Controller-03"], v.AnswerFiles);
        Assert.Equal(["Controller-03"], v.AnswerSymbols);
    }

    // ---------- majority ----------

    private static Ap6RunVerdict Verdict(string task, string arm, int run, bool correct, bool scored = true, long tokens = 100) =>
        new() { TaskId = task, Class = "C", Arm = arm, Run = run, Scored = scored, Correct = correct && scored, TokensApprox = tokens };

    [Theory]
    [InlineData(true, true, true, "Correct")]
    [InlineData(true, true, false, "Correct")]
    [InlineData(true, false, false, "Incorrect")]
    [InlineData(false, false, false, "Incorrect")]
    public void Majority_IsAtLeastTwoOfThreeScoredRuns(bool r1, bool r2, bool r3, string expected)
    {
        var o = Ap6Scorer.ArmOutcome([Verdict("T", "mcp", 1, r1), Verdict("T", "mcp", 2, r2), Verdict("T", "mcp", 3, r3)]);

        Assert.Equal(expected, o.Majority.ToString());
        Assert.Equal(3, o.ScoredRuns);
        Assert.Equal(300, o.TokensApprox);
    }

    [Fact]
    public void FewerThanThreeScoredRuns_IsIncomplete_NeverCorrect()
    {
        var two = Ap6Scorer.ArmOutcome([Verdict("T", "mcp", 1, true), Verdict("T", "mcp", 2, true)]);
        Assert.Equal(Ap6Majority.Incomplete, two.Majority);
        Assert.Equal(2, two.ScoredRuns);
        Assert.Equal(2, two.CorrectRuns);

        // An arm violation removes the run from the count — two correct runs plus a violated one is still incomplete.
        var violated = Ap6Scorer.ArmOutcome([Verdict("T", "mcp", 1, true), Verdict("T", "mcp", 2, true), Verdict("T", "mcp", 3, true, scored: false)]);
        Assert.Equal(Ap6Majority.Incomplete, violated.Majority);
        Assert.Equal(2, violated.ScoredRuns);

        Assert.Equal(Ap6Majority.Incomplete, Ap6Scorer.ArmOutcome([]).Majority);
    }

    [Fact]
    public void Outcomes_PairEachTaskWithBothArms()
    {
        var tasks = new List<Ap6Task> { Ap6Fixtures.Task("C1-01", "C"), Ap6Fixtures.Task("C1-02", "C") };
        var verdicts = new List<Ap6RunVerdict>();
        foreach (var run in new[] { 1, 2, 3 })
        {
            verdicts.Add(Verdict("C1-01", "mcp", run, true));
            verdicts.Add(Verdict("C1-01", "rg", run, false));
            verdicts.Add(Verdict("C1-02", "mcp", run, run == 1));
        }

        var outcomes = Ap6Scorer.Outcomes(tasks, verdicts);

        Assert.Equal(2, outcomes.Count);
        Assert.Equal(Ap6Majority.Correct, outcomes[0].Mcp.Majority);
        Assert.Equal(Ap6Majority.Incorrect, outcomes[0].Rg.Majority);
        Assert.Equal(Ap6Majority.Incorrect, outcomes[1].Mcp.Majority);
        Assert.Equal(Ap6Majority.Incomplete, outcomes[1].Rg.Majority);
    }

    // ---------- the gate ----------

    private static Ap6TaskOutcome Outcome(string id, Ap6Majority mcp, Ap6Majority rg, long mcpTokens = 100, long rgTokens = 1000) =>
        new(id, "C", new Ap6ArmOutcome(mcp, 3, mcp == Ap6Majority.Correct ? 3 : 0, mcpTokens), new Ap6ArmOutcome(rg, 3, rg == Ap6Majority.Correct ? 3 : 0, rgTokens));

    /// <summary><paramref name="mcpCorrect"/> tasks the MCP arm gets, <paramref name="bothCorrect"/> of which the rg arm gets too; the rest the rg arm misses.</summary>
    private static List<Ap6TaskOutcome> ClassC(int mcpCorrect, int bothCorrect, long mcpTokens = 100, long rgTokens = 1000) =>
        Enumerable.Range(1, 10).Select(i => Outcome($"C-{i:00}",
            i <= mcpCorrect ? Ap6Majority.Correct : Ap6Majority.Incorrect,
            i <= bothCorrect ? Ap6Majority.Correct : Ap6Majority.Incorrect,
            mcpTokens, rgTokens)).ToList();

    [Fact]
    public void Gate_PlusTwo_IsNotMet()
    {
        var g = Ap6Scorer.Gate(ClassC(mcpCorrect: 5, bothCorrect: 3));

        Assert.Equal(5, g.CorrectMcp);
        Assert.Equal(3, g.CorrectRg);
        Assert.False(g.CountCondition);
        Assert.True(g.TokenCondition);
        Assert.False(g.Met);
        Assert.StartsWith("not met", g.Decision);
        Assert.Contains("5 − 3 = 2 ≥ 3: no", g.Arithmetic);
    }

    [Fact]
    public void Gate_PlusThree_ButMoreTokens_IsNotMet()
    {
        var g = Ap6Scorer.Gate(ClassC(mcpCorrect: 6, bothCorrect: 3, mcpTokens: 1000, rgTokens: 1000));

        Assert.True(g.CountCondition);
        Assert.False(g.TokenCondition);
        Assert.False(g.Met);
        Assert.StartsWith("not met", g.Decision);
        Assert.Contains("3,000 < Σ tokensApprox(rg) = 3,000", g.Arithmetic);
    }

    [Fact]
    public void Gate_PlusThree_AndFewerTokens_IsMet()
    {
        var g = Ap6Scorer.Gate(ClassC(mcpCorrect: 6, bothCorrect: 3));

        Assert.True(g.CountCondition);
        Assert.True(g.TokenCondition);
        Assert.Equal(3, g.BothCorrect);
        Assert.Equal(300, g.TokensMcp);
        Assert.Equal(3000, g.TokensRg);
        Assert.True(g.Met);
        Assert.Equal("met — phases 2-4 continue", g.Decision);
        Assert.Contains("6 − 3 = 3 ≥ 3: yes", g.Arithmetic);
    }

    [Fact]
    public void Gate_TokenConditionNotEvaluable_IsNotMet()
    {
        // The MCP arm wins every task it gets and the rg arm none: no task is correct in both, so the token
        // condition cannot be evaluated — and a condition that cannot be evaluated is not met.
        var g = Ap6Scorer.Gate(ClassC(mcpCorrect: 4, bothCorrect: 0));

        Assert.True(g.CountCondition);
        Assert.Null(g.TokenCondition);
        Assert.False(g.Met);
        Assert.Contains("not evaluable", g.Arithmetic);
    }

    [Fact]
    public void Gate_IncompleteTasks_CountForNeitherArm()
    {
        var outcomes = ClassC(mcpCorrect: 6, bothCorrect: 3);
        outcomes[0] = Outcome("C-01", Ap6Majority.Incomplete, Ap6Majority.Correct);

        var g = Ap6Scorer.Gate(outcomes);

        Assert.Equal(5, g.CorrectMcp);
        Assert.Equal(3, g.CorrectRg);
        Assert.False(g.Met);
    }

    [Fact]
    public void GateText_IsTheRatifiedSentence()
    {
        Assert.Equal(
            "MCP arm correct on at least 3 more class-C tasks than the rg arm (majority of 3 runs) AND fewer file-content tokens read at equal correctness",
            Ap6Scorer.GateText);
        Assert.Equal(3, Ap6Scorer.GateMargin);
        Assert.Equal(3, Ap6Scorer.RunsPerArm);
    }
}
