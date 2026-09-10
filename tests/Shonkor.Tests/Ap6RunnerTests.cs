// Licensed to Shonkor under the MIT License.

extern alias bench;

using System.Text.Json;
using bench::Shonkor.Bench;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// <see cref="Ap6Runner"/> (#473): the run-directory containment guard of <c>--ap6-plan</c>, and <c>--ap6</c>
/// over a synthetic run directory in a temp folder — the tool-input redaction that decides whether
/// <c>results-A.json</c> can be written at all, corrupt JSON as a finding instead of a crash, and the graph
/// drift note. No API, no scan, no customer data: every path here is invented or a temp directory.
/// </summary>
public class Ap6RunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shonkor-ap6-runner-" + Guid.NewGuid().ToString("N"));

    public Ap6RunnerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ---------- --out containment ----------

    [Fact]
    public void OutDirProblem_InsideOrEqualToTheBrainRoot_IsAProblem()
    {
        var brain = Path.Combine(_root, "brain");

        Assert.Contains("inside the Brain repository", Ap6Runner.OutDirProblem(Path.Combine(brain, "runs", "x"), brain, null));
        Assert.Contains("inside the Brain repository", Ap6Runner.OutDirProblem(brain, brain, null));
        Assert.Contains("inside the Brain repository", Ap6Runner.OutDirProblem(brain + Path.DirectorySeparatorChar, brain, null));
    }

    [Fact]
    public void OutDirProblem_InsideTheCorpusRoot_IsAProblem_WithoutNamingIt()
    {
        var corpus = Path.Combine(_root, "customer-corpus");

        var p = Ap6Runner.OutDirProblem(Path.Combine(corpus, "bench", "runs"), Path.Combine(_root, "brain"), corpus);

        Assert.NotNull(p);
        Assert.Contains("inside the corpus repository", p);
        Assert.DoesNotContain("customer-corpus", p);
    }

    [Fact]
    public void OutDirProblem_ASiblingSharingAPrefix_IsFine()
    {
        var brain = Path.Combine(_root, "brain");

        Assert.Null(Ap6Runner.OutDirProblem(Path.Combine(_root, "brain-runs", "x"), brain, Path.Combine(_root, "corpus")));
        Assert.Null(Ap6Runner.OutDirProblem(Path.Combine(_root, "runs"), brain, null));
    }

    [Fact]
    public void OutDirProblem_ARelativePath_IsResolvedAgainstTheCurrentDirectory()
    {
        // `run.sh runs-rel` with the repository as cwd: the literal does not start with the root, the resolved path does.
        var cwd = Directory.GetCurrentDirectory();

        Assert.Contains("inside the Brain repository", Ap6Runner.OutDirProblem("runs-rel", cwd, null));
        Assert.Contains("inside the Brain repository", Ap6Runner.OutDirProblem(Ap6Runner.ResolveOutDir("runs-rel"), cwd, null));
    }

    [SkippableFact]
    public void ResolveOutDir_ReadsAnMsysDrivePath_AsTheWindowsDrive()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "MSYS drive paths are a Git-Bash-on-Windows shape");
        var brain = Path.Combine(_root, "brain");                                   // C:\Users\...\brain
        var msys = "/" + char.ToLowerInvariant(brain[0]) + brain[2..].Replace('\\', '/') + "/runs";  // /c/Users/.../brain/runs

        var resolved = Ap6Runner.ResolveOutDir(msys);

        Assert.Equal(Path.Combine(brain, "runs"), resolved, ignoreCase: true);
        Assert.Contains("inside the Brain repository", Ap6Runner.OutDirProblem(resolved, brain, null));
    }

    // ---------- --ap6 over a synthetic run directory ----------

    private const string BrainCwd = @"C:\Projects\Brain";

    private (string RunDir, string BrainRoot) WriteRun(string planJson, string? envJson, params (string Task, string Arm, int Run, string Stream)[] streams)
    {
        var brainRoot = Path.Combine(_root, "brain");
        var runDir = Path.Combine(_root, "run");
        Directory.CreateDirectory(runDir);
        File.WriteAllText(Path.Combine(_root, "tasks.json"), JsonSerializer.Serialize(new[] { Ap6Fixtures.Task("A-01", "A", files: ["src/X.cs"], symbols: ["Foo"]) }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        File.WriteAllText(Path.Combine(runDir, Ap6Runner.PlanFile), planJson);
        if (envJson is not null) File.WriteAllText(Path.Combine(runDir, Ap6Runner.EnvFile), envJson);
        foreach (var (task, arm, run, stream) in streams)
        {
            var dir = Path.Combine(runDir, task, arm, run.ToString());
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "stream.jsonl"), stream);
        }
        return (runDir, brainRoot);
    }

    private string PlanJson() => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        tasksPath = Path.Combine(_root, "tasks.json"),
        brainRoot = Path.Combine(_root, "brain"),
        brainProject = "Shonkor",
        tasks = new[] { new { id = "A-01", @class = "A", corpus = "Brain", cwd = BrainCwd, promptFile = "prompts/A-01.txt" } },
    });

    private static async Task<(int Exit, string Console)> ScoreAsync(string runDir)
    {
        using var provider = new SqliteGraphStorageProvider(":memory:");
        await provider.InitializeAsync();
        var console = new StringWriter();
        var exit = await Ap6Runner.ScoreAsync(provider, new Ap6Runner.ScoreOptions(runDir, null, Ap6MatchMode.Recall), console);
        return (exit, console.ToString());
    }

    // ---------- --ap6-tally ----------

    [Fact]
    public void Tally_NamesTheRunsToRedo_AndTheLimitHits_AndWritesResultJson()
    {
        var (runDir, _) = WriteRun(PlanJson(), null,
            ("A-01", "rg", 1, Ap6Fixtures.RgStream(["src/X.cs"], ["Foo"])),
            ("A-01", "rg", 2, Ap6Fixtures.Stream(Ap6Fixtures.Init(Ap6Fixtures.RgTools), Ap6Fixtures.ResultApiError(429, "You've hit your session limit · resets 4pm"))),
            ("A-01", "rg", 3, Ap6Fixtures.Stream(Ap6Fixtures.Init(Ap6Fixtures.RgTools), Ap6Fixtures.ResultLoopError("error_max_turns", "max turns"))),
            ("A-01", "mcp", 1, Ap6Fixtures.Stream(Ap6Fixtures.Init(Ap6Fixtures.McpTools, ("shonkor", "connected")))),
            ("A-01", "mcp", 2, Ap6Fixtures.Stream(Ap6Fixtures.Init(Ap6Fixtures.McpTools, ("shonkor", "connected")), Ap6Fixtures.ResultApiError(500, "Internal server error"))));
        var console = new StringWriter();

        var exit = Ap6Runner.Tally(runDir, console);

        Assert.Equal(0, exit);
        var line = console.ToString().Trim();
        Assert.StartsWith("runs=5 cost_usd=0.0900 without_result=1 ", line);
        Assert.Contains("redo=[A-01/mcp/1,A-01/mcp/2,A-01/rg/2]", line);
        Assert.Contains("limit=[A-01/rg/2]", line);
        // result.json is written wherever a result event exists — the limit hit included, so --resume can see what happened.
        Assert.True(File.Exists(Path.Combine(runDir, "A-01", "rg", "2", "result.json")));
        Assert.True(File.Exists(Path.Combine(runDir, "A-01", "rg", "3", "result.json")));
        Assert.False(File.Exists(Path.Combine(runDir, "A-01", "mcp", "1", "result.json")));
    }

    [Fact]
    public async Task Score_AnRgArmReadByAbsolutePath_StillWritesResultsA_WithTheInputRelative()
    {
        // The tester's reproduction: the rg arm calls Read with an absolute Brain path; before the fix FindLeaks blocked results-A.json.
        var rg = Ap6Fixtures.Stream(
            Ap6Fixtures.Init(Ap6Fixtures.RgTools),
            Ap6Fixtures.ToolUse("Read", new { file_path = BrainCwd + @"\src\X.cs" }),
            Ap6Fixtures.ToolResultString("class Foo {}"),
            Ap6Fixtures.Result(Ap6Fixtures.Answer(["src/X.cs"], ["Foo"])));
        var (runDir, brainRoot) = WriteRun(PlanJson(), null, ("A-01", "rg", 1, rg), ("A-01", "mcp", 1, Ap6Fixtures.McpStream(["src/X.cs"], ["Foo"])));

        var (exit, console) = await ScoreAsync(runDir);

        Assert.Equal(0, exit);
        var results = Path.Combine(brainRoot, "bench", "golden", "ap6", "results-A.json");
        Assert.True(File.Exists(results), console);
        var json = File.ReadAllText(results);
        Assert.Contains(@"src/X.cs", json);
        Assert.DoesNotContain("Projects", json);
        Assert.Empty(Ap6Corpus.FindLeaks(json, null));
        Assert.True(File.Exists(Path.Combine(brainRoot, "bench", "ap6-part1-report.md")));
    }

    [Fact]
    public async Task Score_ACorruptPlanJson_IsAFindingNotACrash()
    {
        var (runDir, brainRoot) = WriteRun("{ \"schemaVersion\": 1, \"tasks\": [ oops", null);

        var (exit, console) = await ScoreAsync(runDir);

        Assert.Equal(0, exit);
        Assert.Contains("not valid JSON", console);
        Assert.Contains("Nothing scored", console);
        Assert.False(Directory.Exists(Path.Combine(brainRoot, "bench")));
    }

    [Fact]
    public async Task Score_ACorruptEnvJson_IsANoteInTheReport()
    {
        var (runDir, brainRoot) = WriteRun(PlanJson(), "{ not json");

        var (exit, _) = await ScoreAsync(runDir);

        Assert.Equal(0, exit);
        var report = File.ReadAllText(Path.Combine(brainRoot, "bench", "ap6-part1-report.md"));
        Assert.Contains("env.json is not valid JSON", report);
    }

    // ---------- graph drift ----------

    private static Ap6GraphState Graph(string name, string? indexed) => new(name, indexed, "fp", 5, 1, 1, [], null);

    [Fact]
    public void GraphDriftNotes_NameTheGraphWhoseRevisionMovedSinceTheRun()
    {
        var env = Ap6Env.Empty with { BrainHead = "de44654380032c1766d089d859c7e3c86ac79a74", CorpusRevision = "9d7f9ce" };
        var graphs = new List<Ap6GraphState>
        {
            Graph("Brain", "0000000000000000000000000000000000000000"),
            Graph(Ap6Runner.CorpusProjectName, "9d7f9ce1234567890abcdef1234567890abcdef1"),
        };

        var notes = Ap6Runner.GraphDriftNotes(graphs, env).ToList();

        var n = Assert.Single(notes);
        Assert.Contains("Brain graph scored at indexedRevision 000000000000", n);
        Assert.Contains("env.brainHead de4465438003", n);
    }

    [Fact]
    public void GraphDriftNotes_AreSilent_WhenNothingIsRecorded_OrNothingMoved()
    {
        var env = Ap6Env.Empty with { BrainHead = "de44654380032c1766d089d859c7e3c86ac79a74" };

        Assert.Empty(Ap6Runner.GraphDriftNotes([Graph("Brain", "de44654380032c1766d089d859c7e3c86ac79a74")], env));
        Assert.Empty(Ap6Runner.GraphDriftNotes([Graph("Brain", null)], env));
        Assert.Empty(Ap6Runner.GraphDriftNotes([Graph("Brain", "abc")], Ap6Env.Empty));
    }
}
