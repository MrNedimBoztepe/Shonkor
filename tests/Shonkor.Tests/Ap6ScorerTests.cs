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

    /// <summary>
    /// The call level, symmetric to the mcp arm's (#514). Being offered only Bash and Read is what
    /// <c>init.tools</c> says; what the arm CALLED is the other fact, and until now nothing looked at it in
    /// this arm — a foreign tool that was never offered would have been counted as clean research.
    /// </summary>
    [Fact]
    public void RgArm_IsViolated_ByCallingAToolItWasNotGiven()
    {
        var r = Ap6RunReader.Read(Ap6Fixtures.Stream(
            Ap6Fixtures.Init(Ap6Fixtures.RgTools),
            Ap6Fixtures.ToolUse("Bash", new { command = "rg -n Foo src" }, "m1"),
            Ap6Fixtures.ToolUse("Grep", new { pattern = "Foo" }, "m2"),
            Ap6Fixtures.Result(Ap6Fixtures.Answer([], []))));

        var v = Ap6Scorer.ArmViolation(r, Ap6Scorer.RgArm);

        Assert.NotNull(v);
        Assert.Contains("Grep", v);
    }

    /// <summary>
    /// The void mechanism rests entirely on <c>permission_denials[].tool_use_id</c>, and MIN_CLAUDE is only
    /// a lower bound: a later CLI that stops emitting the id would make every refused call look executed,
    /// and every properly guarded run would be voided with "rg arm executed N non-rg Bash command(s)" — a
    /// sentence about something that did not happen. The run is still not counted (we do not know), but it
    /// is told apart from an arm that really ran grep (#514).
    /// </summary>
    [Fact]
    public void RgArm_SaysSo_WhenDenialsCannotBeAttributedToACall()
    {
        var r = Ap6RunReader.Read(Ap6Fixtures.Stream(
            Ap6Fixtures.Init(Ap6Fixtures.RgTools),
            Ap6Fixtures.ToolUse("Bash", new { command = "grep -rn Foo src" }, "m1"),
            Ap6Fixtures.Result(Ap6Fixtures.Answer([], []), denials: 1)));

        var v = Ap6Scorer.ArmViolation(r, Ap6Scorer.RgArm);

        Assert.NotNull(v);
        Assert.Contains("could not be attributed", v);
        Assert.DoesNotContain("executed", v);
    }

    // ---------- #512: the answer channel is not a foreign tool ----------

    /// <summary>
    /// The harness passes <c>--json-schema</c>, so the CLI registers <c>StructuredOutput</c> in every run's
    /// <c>init.tools</c>. The first smoke run voided all six runs over it — a tool the harness put there
    /// itself. Both arms must accept exactly it, and nothing else must ride in with it.
    /// </summary>
    [Fact]
    public void BothArms_AcceptTheAnswerTool_InInitTools()
    {
        var mcp = Ap6RunReader.Read(Ap6Fixtures.Stream(
            Ap6Fixtures.Init(Ap6Fixtures.McpTools.Append(Ap6Scorer.AnswerTool), ("shonkor", "connected"))));
        Assert.Null(Ap6Scorer.ArmViolation(mcp, Ap6Scorer.McpArm));

        var rg = Ap6RunReader.Read(Ap6Fixtures.Stream(Ap6Fixtures.Init(Ap6Fixtures.RgTools.Append(Ap6Scorer.AnswerTool))));
        Assert.Null(Ap6Scorer.ArmViolation(rg, Ap6Scorer.RgArm));
    }

    /// <summary>The tolerance is for that one name only — it is not a door held open for the next tool that turns up.</summary>
    [Theory]
    [InlineData("WebFetch")]
    [InlineData("WebSearch")]
    [InlineData("Task")]
    public void BothArms_AreStillViolated_ByAnyOtherForeignTool_BesideTheAnswerTool(string tool)
    {
        var mcp = Ap6RunReader.Read(Ap6Fixtures.Stream(
            Ap6Fixtures.Init(Ap6Fixtures.McpTools.Append(Ap6Scorer.AnswerTool).Append(tool), ("shonkor", "connected"))));
        var vm = Ap6Scorer.ArmViolation(mcp, Ap6Scorer.McpArm);
        Assert.NotNull(vm);
        Assert.Contains(tool, vm);
        Assert.DoesNotContain(Ap6Scorer.AnswerTool, vm, StringComparison.Ordinal);

        var rg = Ap6RunReader.Read(Ap6Fixtures.Stream(Ap6Fixtures.Init(Ap6Fixtures.RgTools.Append(Ap6Scorer.AnswerTool).Append(tool))));
        var vr = Ap6Scorer.ArmViolation(rg, Ap6Scorer.RgArm);
        Assert.NotNull(vr);
        Assert.Contains(tool, vr);
        Assert.DoesNotContain(Ap6Scorer.AnswerTool, vr, StringComparison.Ordinal);
    }

    /// <summary>Empty, and the fix for #512 did not fill it: a general tolerance bucket is what that issue rules out.</summary>
    [Fact]
    public void ToleratedTools_StaysEmpty()
    {
        Assert.Empty(Ap6Scorer.ToleratedTools);
        Assert.DoesNotContain(Ap6Scorer.AnswerTool, Ap6Scorer.RgArmTools);
    }

    /// <summary>A run in the exact shape a real one has — the arm's tools plus the answer channel — is scored.</summary>
    [Fact]
    public void ARunWithTheArmsToolsPlusTheAnswerTool_IsScored_AndTheEmissionIsNoResearchStep()
    {
        var task = Ap6Fixtures.Task("A-01", "A", files: ["src/X/Foo.cs"], symbols: ["Foo"]);
        var stream = Ap6Fixtures.Stream(
            Ap6Fixtures.Init(Ap6Fixtures.RgTools.Append(Ap6Scorer.AnswerTool)),
            Ap6Fixtures.ToolUse("Bash", new { command = "rg -n Foo src" }, "m1"),
            Ap6Fixtures.ToolResultString("src/X/Foo.cs:1:class Foo"),
            Ap6Fixtures.AnswerEmission(["src/X/Foo.cs"], ["Foo"]),
            Ap6Fixtures.ToolResultString("answer recorded", toolUseId: "t_m_ans"),
            Ap6Fixtures.Result(Ap6Fixtures.Answer(["src/X/Foo.cs"], ["Foo"])));

        var record = Ap6RunReader.Read(stream);
        var v = Ap6Scorer.Score(task, Ap6Scorer.RgArm, 1, record, Ap6MatchMode.Recall, Cwd, null);

        Assert.Null(v.ArmViolation);
        Assert.True(v.Scored);
        Assert.True(v.Correct);
        // One research step, not two: the emission is counted apart and its echo is not context the arm read.
        Assert.Equal(1, v.ToolCalls);
        Assert.Single(record.AnswerEmissions);
        Assert.Equal("src/X/Foo.cs:1:class Foo".Length, record.ToolResultChars);
    }

    // ---------- #513: what the arm did, not only what it was offered ----------

    /// <summary>
    /// #513: the rg arm ran <c>grep</c> and would have been scored, because the check only ever looked at
    /// <c>init.tools</c>. A command that executed outside the arm voids the run, whatever init said.
    /// </summary>
    [Fact]
    public void RgArm_IsViolated_ByANonRgBashCommandThatRan()
    {
        var r = Ap6RunReader.Read(Ap6Fixtures.Stream(
            Ap6Fixtures.Init(Ap6Fixtures.RgTools),
            Ap6Fixtures.ToolUse("Bash", new { command = "grep -rn Foo src" }, "m1"),
            Ap6Fixtures.ToolResultString("src/X/Foo.cs:1:class Foo"),
            Ap6Fixtures.Result(Ap6Fixtures.Answer(["src/X/Foo.cs"], ["Foo"]))));

        Assert.Equal(1, r.BashNonRg);
        Assert.Contains("executed 1 non-rg Bash", Ap6Scorer.ArmViolation(r, Ap6Scorer.RgArm)!);

        var v = Ap6Scorer.Score(Ap6Fixtures.Task("A-01", "A"), Ap6Scorer.RgArm, 1, r, Ap6MatchMode.Recall, Cwd, null);
        Assert.False(v.Scored);
        Assert.False(v.Correct);
    }

    /// <summary>
    /// The other half, and the one that is easy to get wrong: a call the hook refused is the guard working.
    /// Voiding those runs would discard exactly the evidence that the rg arm was held to ripgrep, and with
    /// fewer than three scored runs the whole task falls out of the majority.
    /// </summary>
    [Fact]
    public void RgArm_IsNotViolated_ByANonRgBashCommandTheHookRefused()
    {
        var r = Ap6RunReader.Read(Ap6Fixtures.Stream(
            Ap6Fixtures.Init(Ap6Fixtures.RgTools),
            Ap6Fixtures.ToolUse("Bash", new { command = "grep -rn Foo src" }, "m1"),
            Ap6Fixtures.ToolResultString("PreToolUse:Bash hook error: rg-only arm: 'grep' is not ripgrep."),
            Ap6Fixtures.ToolUse("Bash", new { command = "rg -n Foo src" }, "m2"),
            Ap6Fixtures.ToolResultString("src/X/Foo.cs:1:class Foo", toolUseId: "t_m2"),
            Ap6Fixtures.Result(Ap6Fixtures.Answer(["src/X/Foo.cs"], ["Foo"]), deniedToolUseIds: "t_m1")));

        Assert.Equal(0, r.BashNonRg);
        Assert.Equal(1, r.BashNonRgDenied);
        Assert.Null(Ap6Scorer.ArmViolation(r, Ap6Scorer.RgArm));

        var v = Ap6Scorer.Score(Ap6Fixtures.Task("A-01", "A"), Ap6Scorer.RgArm, 1, r, Ap6MatchMode.Recall, Cwd, null);
        Assert.True(v.Scored);
        Assert.True(v.Correct);
        Assert.Equal(1, v.BashNonRgDenied);
    }

    /// <summary>A denial we cannot attribute to a call leaves the call counted as executed — the run is voided rather than trusted.</summary>
    [Fact]
    public void ADenialWithoutAToolUseId_DoesNotExcuseTheCall()
    {
        var r = Ap6RunReader.Read(Ap6Fixtures.Stream(
            Ap6Fixtures.Init(Ap6Fixtures.RgTools),
            Ap6Fixtures.ToolUse("Bash", new { command = "grep -rn Foo src" }, "m1"),
            Ap6Fixtures.Result(Ap6Fixtures.Answer([], []), denials: 1)));

        Assert.Equal(1, r.BashNonRg);
        Assert.Equal(0, r.BashNonRgDenied);
        Assert.NotNull(Ap6Scorer.ArmViolation(r, Ap6Scorer.RgArm));
    }

    /// <summary>The mirror image: the mcp arm calling anything that is not a shonkor tool.</summary>
    [Fact]
    public void McpArm_IsViolated_ByACallToANonShonkorTool()
    {
        var r = Ap6RunReader.Read(Ap6Fixtures.Stream(
            Ap6Fixtures.Init(Ap6Fixtures.McpTools.Append("Read"), ("shonkor", "connected")),
            Ap6Fixtures.ToolUse("Read", new { file_path = "src/X/Foo.cs" }, "m1"),
            Ap6Fixtures.Result(Ap6Fixtures.Answer([], []))));

        // init.tools already gives it away here; the call-level check is what catches a tool that was never offered.
        var offered = Ap6RunReader.Read(Ap6Fixtures.Stream(
            Ap6Fixtures.Init(Ap6Fixtures.McpTools, ("shonkor", "connected")),
            Ap6Fixtures.ToolUse("Read", new { file_path = "src/X/Foo.cs" }, "m1"),
            Ap6Fixtures.Result(Ap6Fixtures.Answer([], []))));

        Assert.NotNull(Ap6Scorer.ArmViolation(r, Ap6Scorer.McpArm));
        Assert.Contains("called non-shonkor tool(s): Read", Ap6Scorer.ArmViolation(offered, Ap6Scorer.McpArm)!);
    }

    /// <summary>The answer emission is not a call the mcp arm made outside its arm — it is how it answered.</summary>
    [Fact]
    public void McpArm_IsNotViolated_ByItsOwnAnswerEmission()
    {
        var r = Ap6RunReader.Read(Ap6Fixtures.Stream(
            Ap6Fixtures.Init(Ap6Fixtures.McpTools.Append(Ap6Scorer.AnswerTool), ("shonkor", "connected")),
            Ap6Fixtures.ToolUse("mcp__shonkor__locate", new { query = "Foo" }, "m1"),
            Ap6Fixtures.AnswerEmission(["src/X/Foo.cs"], ["Foo"]),
            Ap6Fixtures.Result(Ap6Fixtures.Answer(["src/X/Foo.cs"], ["Foo"]))));

        Assert.Null(Ap6Scorer.ArmViolation(r, Ap6Scorer.McpArm));
        Assert.Single(r.ToolCalls);
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

    // ---------- not run (usage limit, API error, no result) ----------

    private static Ap6RunVerdict ScoreRg(Ap6Task task, params string[] events) =>
        Ap6Scorer.Score(task, Ap6Scorer.RgArm, 1, Ap6RunReader.Read(Ap6Fixtures.Stream(events)), Ap6MatchMode.Recall, Cwd, null);

    [Theory]
    [InlineData(429, "You've hit your session limit · resets 4pm")]
    [InlineData(429, "")]
    [InlineData(400, "You've hit your weekly limit · resets Mon 12:00am")]
    [InlineData(529, "Request rejected (429)")]
    [InlineData(503, "upstream rate limit exceeded")]
    public void AUsageOrRateLimit_IsNotRun_NeverNoAnswer_AndStartsWithTheLimitPrefix(int status, string text)
    {
        var task = Ap6Fixtures.Task("A-01", "A");

        var v = ScoreRg(task, Ap6Fixtures.Init(Ap6Fixtures.RgTools), Ap6Fixtures.ResultApiError(status, text));

        Assert.True(v.NotRun);
        Assert.False(v.Scored);
        Assert.False(v.NoAnswer);
        Assert.False(v.Correct);
        Assert.Null(v.ArmViolation);
        Assert.True(v.IsError);
        Assert.StartsWith(Ap6Scorer.LimitReasonPrefix, v.NotRunReason);
        Assert.True(Ap6Scorer.IsLimitReason(v.NotRunReason));
        Assert.Contains($"HTTP {status}", v.NotRunReason);
    }

    [Fact]
    public void AnotherApiError_OrALoopFailure_OrNoResultEvent_IsNotRun_ButNoLimit()
    {
        var task = Ap6Fixtures.Task("A-01", "A");

        var server = ScoreRg(task, Ap6Fixtures.Init(Ap6Fixtures.RgTools), Ap6Fixtures.ResultApiError(500, "Internal server error"));
        Assert.True(server.NotRun);
        Assert.False(Ap6Scorer.IsLimitReason(server.NotRunReason));
        Assert.Contains("HTTP 500", server.NotRunReason);

        var loop = ScoreRg(task, Ap6Fixtures.Init(Ap6Fixtures.RgTools), Ap6Fixtures.ResultLoopError("error_during_execution", "sandbox failed to start"));
        Assert.True(loop.NotRun);
        Assert.False(Ap6Scorer.IsLimitReason(loop.NotRunReason));
        Assert.Contains("error_during_execution", loop.NotRunReason);
        Assert.Contains("sandbox failed to start", loop.NotRunReason);

        // The process died before its result event: not the arm's doing, so not a measurement — and no arm violation
        // is derived from a stream that never started (the init event may be missing as well).
        var dead = ScoreRg(task, Ap6Fixtures.ToolUse("Bash", new { command = "rg -n Foo src" }));
        Assert.True(dead.NotRun);
        Assert.False(dead.Scored);
        Assert.Null(dead.ArmViolation);
        Assert.Contains("without a result event", dead.NotRunReason);
    }

    [Theory]
    [InlineData("error_max_turns")]
    [InlineData("error_max_budget_usd")]
    [InlineData("error_max_structured_output_retries")]
    public void ThePinnedLimits_StayScored_AsNoAnswer(string subtype)
    {
        // These end the run by the limits of #505 — the arm's outcome under them is the measurement.
        var task = Ap6Fixtures.Task("A-01", "A");

        var v = ScoreRg(task, Ap6Fixtures.Init(Ap6Fixtures.RgTools), Ap6Fixtures.ResultLoopError(subtype, "limit reached"));

        Assert.False(v.NotRun);
        Assert.True(v.Scored);
        Assert.True(v.NoAnswer);
        Assert.False(v.Correct);
        Assert.True(v.IsError);
    }

    [Fact]
    public void ANotRunRun_LeavesTheArmIncomplete_UntilItIsRunAgain()
    {
        var task = Ap6Fixtures.Task("A-01", "A", files: ["src/X/Foo.cs"], symbols: ["Foo"]);
        var good = Ap6RunReader.Read(Ap6Fixtures.RgStream(["src/X/Foo.cs"], ["Foo"]));
        var limited = Ap6RunReader.Read(Ap6Fixtures.Stream(Ap6Fixtures.Init(Ap6Fixtures.RgTools), Ap6Fixtures.ResultApiError(429, "You've hit your session limit · resets 4pm")));
        var verdicts = new[]
        {
            Ap6Scorer.Score(task, Ap6Scorer.RgArm, 1, good, Ap6MatchMode.Recall, Cwd, null),
            Ap6Scorer.Score(task, Ap6Scorer.RgArm, 2, good, Ap6MatchMode.Recall, Cwd, null),
            Ap6Scorer.Score(task, Ap6Scorer.RgArm, 3, limited, Ap6MatchMode.Recall, Cwd, null),
        };

        var o = Ap6Scorer.ArmOutcome(verdicts);

        Assert.Equal(Ap6Majority.Incomplete, o.Majority);
        Assert.Equal(2, o.ScoredRuns);
        Assert.Equal(2, o.CorrectRuns);
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
