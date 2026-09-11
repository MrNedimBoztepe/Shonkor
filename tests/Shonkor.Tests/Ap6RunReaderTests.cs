// Licensed to Shonkor under the MIT License.

extern alias bench;

using System.Text.Json;
using bench::Shonkor.Bench;

namespace Shonkor.Tests;

/// <summary>
/// <see cref="Ap6RunReader"/> over synthetic <c>stream.jsonl</c> text (#473): which events are counted,
/// which are not (subagents, malformed lines), and how the answer is read out of the last result.
/// </summary>
public class Ap6RunReaderTests
{
    [Fact]
    public void Init_IsReadIntoToolsServersAndModel()
    {
        var r = Ap6RunReader.Read(Ap6Fixtures.Stream(Ap6Fixtures.Init(Ap6Fixtures.McpTools, ("shonkor", "connected"), ("other", "failed"))));

        Assert.True(r.SawInit);
        Assert.Equal("claude-test", r.Model);
        Assert.Equal(Ap6Fixtures.McpTools, r.InitTools);
        Assert.Equal("connected", r.McpServers["shonkor"]);
        Assert.Equal("failed", r.McpServers["other"]);
        Assert.False(r.SawResult);
    }

    [Fact]
    public void ToolCalls_OfTheMainConversationOnly_AreCounted()
    {
        var stream = Ap6Fixtures.Stream(
            Ap6Fixtures.Init(Ap6Fixtures.RgTools),
            Ap6Fixtures.ToolUse("Bash", new { command = "rg -n Foo src" }, "m1"),
            Ap6Fixtures.ToolResultString("src/A.cs:1:Foo"),
            Ap6Fixtures.ToolUse("Read", new { file_path = "src/A.cs" }, "m2", parentToolUseId: "t_sub"),
            Ap6Fixtures.ToolResultString("hidden from the arm", parentToolUseId: "t_sub"),
            Ap6Fixtures.Result(Ap6Fixtures.Answer(["src/A.cs"], ["Foo"])));

        var r = Ap6RunReader.Read(stream);

        Assert.Single(r.ToolCalls);
        Assert.Equal("Bash", r.ToolCalls[0].Name);
        Assert.Contains("rg -n Foo src", r.ToolCalls[0].Input);
        Assert.Equal("src/A.cs:1:Foo".Length, r.ToolResultChars);
    }

    [Fact]
    public void ToolResultChars_SumStringAndBlockContent_TokensApproxIsAQuarter()
    {
        var stream = Ap6Fixtures.Stream(
            Ap6Fixtures.Init(Ap6Fixtures.McpTools, ("shonkor", "connected")),
            Ap6Fixtures.ToolUse("mcp__shonkor__locate", new { query = "Foo" }),
            Ap6Fixtures.ToolResultBlocks(new string('a', 10), new string('b', 12)),
            Ap6Fixtures.ToolUse("mcp__shonkor__get_source", new { symbol = "Foo" }, "m2"),
            Ap6Fixtures.ToolResultString(new string('c', 5)),
            Ap6Fixtures.Result(Ap6Fixtures.Answer([], [])));

        var r = Ap6RunReader.Read(stream);

        Assert.Equal(27, r.ToolResultChars);
        Assert.Equal(7, r.TokensApprox); // 27 / 4 = 6.75 → 7
    }

    [Fact]
    public void UsageExact_CountsEachAssistantMessageOnce()
    {
        // One message streamed as two events (same id) — the usage must not be doubled; a second message adds.
        var stream = Ap6Fixtures.Stream(
            Ap6Fixtures.Init(Ap6Fixtures.McpTools, ("shonkor", "connected")),
            Ap6Fixtures.Text("thinking", "m1", inputTokens: 100, outputTokens: 20),
            Ap6Fixtures.ToolUse("mcp__shonkor__locate", new { query = "Foo" }, "m1", inputTokens: 100, outputTokens: 20),
            Ap6Fixtures.ToolResultString("x"),
            Ap6Fixtures.Text("done", "m2", inputTokens: 300, outputTokens: 40),
            Ap6Fixtures.Result(Ap6Fixtures.Answer([], [])));

        var r = Ap6RunReader.Read(stream);

        Assert.Equal(120 + 340, r.UsageExact);
    }

    [Fact]
    public void Result_IsReadIntoCostTurnsDenialsAndAnswer()
    {
        var stream = Ap6Fixtures.Stream(
            Ap6Fixtures.Init(Ap6Fixtures.RgTools),
            Ap6Fixtures.Result(Ap6Fixtures.Answer(["src/A.cs"], ["Foo.Bar"]), cost: 0.1234, turns: 7, durationMs: 9876, denials: 2));

        var r = Ap6RunReader.Read(stream);

        Assert.True(r.SawResult);
        Assert.Equal("success", r.ResultSubtype);
        Assert.False(r.IsError);
        Assert.Equal(0.1234, r.CostUsd, 6);
        Assert.Equal(7, r.Turns);
        Assert.Equal(9876, r.DurationMs);
        Assert.Equal(2, r.PermissionDenials);
        Assert.NotNull(r.Answer);
        Assert.Equal(1, r.Answer!.SchemaVersion);
        Assert.Equal(["src/A.cs"], r.Answer.Files);
        Assert.Equal(["Foo.Bar"], r.Answer.Symbols);
    }

    [Fact]
    public void TheLastResultEvent_Wins()
    {
        var stream = Ap6Fixtures.Stream(
            Ap6Fixtures.Init(Ap6Fixtures.RgTools),
            Ap6Fixtures.Result(Ap6Fixtures.Answer(["first.cs"], []), cost: 0.1),
            Ap6Fixtures.Result(Ap6Fixtures.Answer(["second.cs"], []), cost: 0.2));

        var r = Ap6RunReader.Read(stream);

        Assert.Equal(0.2, r.CostUsd, 6);
        Assert.Equal(["second.cs"], r.Answer!.Files);
    }

    [Fact]
    public void AFailedFinalRequest_CarriesStatusAndErrorText_ALoopErrorItsErrors()
    {
        var api = Ap6RunReader.Read(Ap6Fixtures.Stream(
            Ap6Fixtures.Init(Ap6Fixtures.RgTools),
            Ap6Fixtures.ResultApiError(429, "You've hit your session limit · resets 4pm")));
        Assert.True(api.SawResult);
        Assert.True(api.IsError);
        Assert.Equal("success", api.ResultSubtype);
        Assert.Equal(429, api.ApiErrorStatus);
        Assert.Equal("api_error", api.TerminalReason);
        Assert.Equal("You've hit your session limit · resets 4pm", api.ErrorText);
        Assert.Null(api.Answer);

        var loop = Ap6RunReader.Read(Ap6Fixtures.Stream(
            Ap6Fixtures.Init(Ap6Fixtures.RgTools),
            Ap6Fixtures.ResultLoopError("error_during_execution", "sandbox failed to start", "second")));
        Assert.Null(loop.ApiErrorStatus);
        Assert.Equal("sandbox failed to start | second", loop.ErrorText);

        // A clean success keeps its result text out of ErrorText — that field is the final assistant message, not an error.
        var ok = Ap6RunReader.Read(Ap6Fixtures.RgStream(["src/X/Foo.cs"], ["Foo"]));
        Assert.Null(ok.ErrorText);
        Assert.Null(ok.ApiErrorStatus);
    }

    [Fact]
    public void ResultWithoutStructuredOutput_HasNoAnswer_AndIsErrorIsKept()
    {
        var stream = Ap6Fixtures.Stream(
            Ap6Fixtures.Init(Ap6Fixtures.RgTools),
            Ap6Fixtures.Result(null, isError: true));

        var r = Ap6RunReader.Read(stream);

        Assert.True(r.SawResult);
        Assert.True(r.IsError);
        Assert.Equal("error_max_turns", r.ResultSubtype);
        Assert.Null(r.StructuredOutputJson);
        Assert.Null(r.Answer);
    }

    /// <summary>
    /// The reader's idea of "an rg command" must be the hook's idea of it
    /// (<c>bench/golden/ap6/rg-only-hook.sh</c>, #513): the hook decides what runs, this decides what the run
    /// counts as, and a gap between the two is how a grep gets executed and scored as clean. Both sides now
    /// read the same fixture — <see cref="Ap6RgCommandCases"/> — instead of each carrying its own list, which
    /// is what let three cases drift apart before #514.
    /// </summary>
    public static TheoryData<string, bool> SharedCases()
    {
        var data = new TheoryData<string, bool>();
        foreach (var (command, allow) in Ap6RgCommandCases.Load()) data.Add(command, allow);
        return data;
    }

    [Theory]
    [MemberData(nameof(SharedCases))]
    public void IsRgCommand_MatchesTheSharedCaseTable(string command, bool expected)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { command }));
        Assert.Equal(expected, Ap6RunReader.IsRgCommand(doc.RootElement));
        Assert.Equal(expected, Ap6RunReader.IsRgCommand(command));
    }

    /// <summary>
    /// The table is the guarantee, so it must keep covering the cases that were measured to matter: the three
    /// former hook/reader divergences, the flags that make ripgrep run a program (#514), and the quoted-pipe
    /// case the rg arm lost turns to. Deleting a row must be a test failure, not a quiet loss of coverage.
    /// </summary>
    [Fact]
    public void TheSharedCaseTable_KeepsTheCasesThatWereMeasured()
    {
        var cases = Ap6RgCommandCases.Load();
        var byCommand = cases.ToDictionary(c => c.Command, c => c.Allow, StringComparer.Ordinal);

        Assert.True(cases.Count >= 40, $"the case table has shrunk to {cases.Count} rows");
        Assert.Contains(cases, c => c.Allow);
        Assert.Contains(cases, c => !c.Allow);
        Assert.True(byCommand["rg -n \"a|b\" src"], "a pipe inside a quoted pattern is one rg command (#514)");
        Assert.True(byCommand["rg -n PARSE-ERROR src"], "the hook's old parse sentinel refused this text (#514)");
        Assert.True(byCommand["rg\vfoo"], "0x0B is a word boundary on both sides");
        Assert.False(byCommand["\u00a0rg Foo"], "U+00A0 is not a space, so this names no ripgrep");
        Assert.False(byCommand["rg --pre /bin/sh --pre-glob '*' Foo"]);
        Assert.False(byCommand["rg -z Foo src"]);
        Assert.False(byCommand["rg -nz Foo src"]);
        Assert.False(byCommand["rg --hostname-bin /bin/sh Foo"]);
        Assert.False(byCommand[string.Empty]);
    }

    /// <summary>An input that carries no <c>command</c> at all is not an rg command — the reader's equivalent of the hook's unreadable payload.</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"file_path":"src/A.cs"}""")]
    [InlineData("""{"command":null}""")]
    public void IsRgCommand_WithoutACommandField_IsFalse(string input)
    {
        using var doc = JsonDocument.Parse(input);
        Assert.False(Ap6RunReader.IsRgCommand(doc.RootElement));
    }

    [Fact]
    public void BashNonRg_CountsBashCallsThatDoNotStartWithRg()
    {
        var stream = Ap6Fixtures.Stream(
            Ap6Fixtures.Init(Ap6Fixtures.RgTools),
            Ap6Fixtures.ToolUse("Bash", new { command = "rg -n Foo" }, "m1"),
            Ap6Fixtures.ToolUse("Bash", new { command = "ls src" }, "m2"),
            Ap6Fixtures.ToolUse("Bash", new { command = "cat src/A.cs" }, "m3"),
            Ap6Fixtures.ToolUse("Read", new { file_path = "src/A.cs" }, "m4"),
            Ap6Fixtures.Result(Ap6Fixtures.Answer([], [])));

        var r = Ap6RunReader.Read(stream);

        Assert.Equal(4, r.ToolCalls.Count);
        Assert.Equal(2, r.BashNonRg);
    }

    [Fact]
    public void McpOverflow_CountsTheStandInText()
    {
        var stream = Ap6Fixtures.Stream(
            Ap6Fixtures.Init(Ap6Fixtures.McpTools, ("shonkor", "connected")),
            Ap6Fixtures.ToolUse("mcp__shonkor__get_subgraph", new { seeds = new[] { "@/x" } }),
            Ap6Fixtures.ToolResultBlocks("MCP tool response (120000 characters) EXCEEDS maximum allowed tokens (25000)."),
            Ap6Fixtures.ToolUse("mcp__shonkor__locate", new { query = "Foo" }, "m2"),
            Ap6Fixtures.ToolResultBlocks("Foo -> src/A.cs:1"),
            Ap6Fixtures.Result(Ap6Fixtures.Answer([], [])));

        var r = Ap6RunReader.Read(stream);

        Assert.Equal(1, r.McpOverflow);
    }

    [Fact]
    public void MalformedLines_AreCountedNotThrown()
    {
        var stream = "not json\n" + Ap6Fixtures.Init(Ap6Fixtures.RgTools) + "\n[1,2]\n\n" + Ap6Fixtures.Result(Ap6Fixtures.Answer([], [])) + "\n";

        var r = Ap6RunReader.Read(stream);

        Assert.Equal(2, r.MalformedLines);
        Assert.True(r.SawInit);
        Assert.True(r.SawResult);
    }

    [Fact]
    public void EmptyStream_IsAnEmptyRecord()
    {
        var r = Ap6RunReader.Read(string.Empty);

        Assert.False(r.SawInit);
        Assert.False(r.SawResult);
        Assert.Empty(r.ToolCalls);
        Assert.Null(r.Answer);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("{\"schemaVersion\":1,\"files\":[]}")]                 // symbols missing
    [InlineData("{\"schemaVersion\":1,\"files\":\"a.cs\",\"symbols\":[]}")] // files not an array
    [InlineData("{not json")]
    public void ParseAnswer_RejectsAnythingThatIsNotTheShape(string? json)
    {
        Assert.Null(Ap6RunReader.ParseAnswer(json));
    }

    [Fact]
    public void ParseAnswer_KeepsStringsOnly_AndANonNumericVersionIsNull()
    {
        var a = Ap6RunReader.ParseAnswer("{\"schemaVersion\":\"1\",\"files\":[\"a.cs\",3,null],\"symbols\":[\"Foo\"]}");

        Assert.NotNull(a);
        Assert.Null(a!.SchemaVersion);
        Assert.Equal(["a.cs"], a.Files);
        Assert.Equal(["Foo"], a.Symbols);
    }
}
