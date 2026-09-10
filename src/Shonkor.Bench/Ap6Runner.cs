// Licensed to Shonkor under the MIT License.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Bench;

/// <summary>
/// The file-and-database side of the AP6 two-arm harness (#473): <c>--ap6-plan</c> (preconditions,
/// prompts, plan files for <c>run.sh</c>), <c>--ap6-tally</c> (cost so far, for the driver's cap) and
/// <c>--ap6</c> (score a run directory, write the report and the pinned results). Decisions live in
/// <see cref="Ap6Preconditions"/>, <see cref="Ap6Scorer"/>, <see cref="Ap6Anonymiser"/> and
/// <see cref="Ap6Report"/>; this class only fetches and writes.
///
/// <para>
/// The run directory is outside both repositories: class-C prompts and streams carry customer names.
/// Only <c>bench/ap6-part1-report.md</c> and <c>bench/golden/ap6/results-&lt;class&gt;.json</c> are written
/// into the Brain checkout, and the class-C one passes <see cref="Ap6Corpus.FindLeaks"/> first.
/// </para>
/// </summary>
internal static class Ap6Runner
{
    public const string CorpusProjectName = "Corpus-A";
    public const string PlanFile = "plan.json";
    public const string EnvFile = "env.json";
    public const string ReportRelativePath = "bench/ap6-part1-report.md";
    public const string ResultsRelativeDir = "bench/golden/ap6";

    /// <summary>The prompt suffix both arms receive after the query — identical on purpose; the arms differ in tools only.</summary>
    public const string PromptTemplateRelativePath = "bench/golden/ap6/prompt-template.txt";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    public sealed record PlanOptions(string TasksPath, string Workspace, string OutDir, string? MappingPath, string? ClassFilter, bool IgnorePreconditions = false);
    public sealed record ScoreOptions(string RunDir, string? CorpusDbPath, Ap6MatchMode Mode);

    /// <summary>What <c>--ap6-plan</c> wrote and <c>--ap6</c> reads back — the paths of one run, kept out of the repository.</summary>
    internal sealed record Plan(
        int SchemaVersion,
        string TasksPath,
        string BrainRoot,
        string BrainProject,
        string? BrainDb,
        string? CorpusRoot,
        string? CorpusDb,
        string? MappingPath,
        string? ClassFilter,
        List<PlanTask> Tasks);

    internal sealed record PlanTask(string Id, string Class, string Corpus, string Cwd, string PromptFile);

    // ---------- --ap6-plan ----------

    public static async Task<int> PlanAsync(PlanOptions o, TextWriter console)
    {
        var workspace = Path.GetFullPath(o.Workspace);
        var tasksPath = Path.GetFullPath(o.TasksPath);
        if (!File.Exists(tasksPath)) { console.WriteLine($"[Error] tasks file not found at '{tasksPath}'."); return 1; }
        var templatePath = Path.Combine(workspace, PromptTemplateRelativePath);
        if (!File.Exists(templatePath)) { console.WriteLine($"[Error] prompt template not found at '{templatePath}'."); return 1; }

        var tasks = Ap6Corpus.Load(tasksPath);
        var problems = Ap6Corpus.Validate(tasks).ToList();
        if (problems.Count > 0)
        {
            console.WriteLine($"[Error] tasks.json is not scoreable ({problems.Count} problem(s)):");
            foreach (var p in problems) console.WriteLine($"  - {p}");
            return 1;
        }
        if (o.ClassFilter is not null) tasks = tasks.Where(t => t.Class == o.ClassFilter).ToList();
        if (tasks.Count == 0) { console.WriteLine($"[Error] no tasks of class '{o.ClassFilter}'."); return 1; }

        // Project roots and databases by NAME from projects.json — never a hardcoded path (the corpus path is customer-specific).
        var pm = new ProjectManager(workspace);
        var brain = pm.FindProjectByPath(workspace) ?? pm.GetProjects().FirstOrDefault(p => FilePaths.AreEqual(Path.GetFullPath(p.Path), workspace));
        if (brain is null) { console.WriteLine($"[Error] projects.json in '{workspace}' has no project whose path is the workspace."); return 1; }
        var corpus = pm.GetProjects().FirstOrDefault(p => p.Name == CorpusProjectName);
        var needCorpus = tasks.Any(t => t.Class == "C");
        if (needCorpus && corpus is null) { console.WriteLine($"[Error] projects.json has no project named '{CorpusProjectName}' — class C needs it (or run with --class A|B)."); return 1; }

        // Before anything is written: the run directory must lie outside both checkouts (class-C prompts carry customer names).
        var outDir = ResolveOutDir(o.OutDir);
        if (OutDirProblem(outDir, workspace, corpus?.Path) is { } outProblem) { console.WriteLine($"[Error] {outProblem} Nothing written."); return 1; }

        var mappingPath = o.MappingPath ?? (corpus is null ? null : Path.Combine(corpus.Path, "bench", "ap6-mapping.json"));
        Ap6Mapping? mapping = null;
        if (needCorpus)
        {
            if (mappingPath is null || !File.Exists(mappingPath))
            {
                // The mapping lives under the corpus root: print it relative to <corpus>, the root itself stays off stdout.
                var shownMapping = mappingPath is null ? "(none)" : Ap6Anonymiser.RedactArgument(mappingPath, new Ap6Mapping(null, corpus?.Path, null, null, null));
                console.WriteLine($"[Error] class C mapping not found at '{shownMapping}' — generate it with bench/golden/ap6/scripts/keys-c.sh <corpus-root>.");
                if (!o.IgnorePreconditions) return 1;
                console.WriteLine("[dry-run] class C tasks dropped from the plan — no mapping to resolve their prompts.");
                tasks = tasks.Where(t => t.Class != "C").ToList();
            }
            else mapping = Ap6Mapping.Load(mappingPath);
        }

        var world = new Ap6Preconditions.World(
            BrainHead: GitHead(workspace),
            BrainIndexedRevision: await IndexedRevisionAsync(brain.DatabasePath).ConfigureAwait(false),
            BrainDirtyCsFiles: GitDirtyFiles(workspace, ".cs"),
            BrainKeyFileExists: f => File.Exists(Path.Combine(workspace, f.Replace('/', Path.DirectorySeparatorChar))),
            CorpusHead: corpus is null ? null : GitHead(corpus.Path),
            CorpusIndexedRevision: corpus is null ? null : await IndexedRevisionAsync(corpus.DatabasePath).ConfigureAwait(false),
            CorpusDirtyFiles: corpus is null ? [] : GitModifiedTracked(corpus.Path, "*.yml", "*.cs", "*.cshtml", "*.sln"));

        var failures = Ap6Preconditions.Check(tasks, mapping, world);
        if (failures.Count > 0)
        {
            console.WriteLine($"[Error] preconditions not met ({failures.Count}):");
            foreach (var f in failures) console.WriteLine($"  - {f}");
            if (!o.IgnorePreconditions) return 1;
            console.WriteLine("[dry-run] --ignore-preconditions: writing the plan anyway; a real run must not pass this flag.");
        }

        Directory.CreateDirectory(Path.Combine(outDir, "prompts"));
        var template = File.ReadAllText(templatePath);
        var planTasks = new List<PlanTask>();
        var tokensResolved = 0;
        var tsv = new StringBuilder();
        foreach (var t in tasks)
        {
            var query = t.Query!;
            if (t.Class == "C")
            {
                try { query = Ap6Anonymiser.ResolveQuery(query, mapping!); }
                catch (InvalidOperationException ex)
                {
                    // Only reachable with --ignore-preconditions (CheckTokens reports it first): drop the task rather than prompt with a token.
                    console.WriteLine($"[dry-run] {t.Id} dropped: {ex.Message}");
                    continue;
                }
                tokensResolved += Ap6Anonymiser.TokensIn(t.Query!).Count;
            }
            var promptFile = $"prompts/{t.Id}.txt";
            // LF only: the prompt goes through stdin on Windows and Linux alike; CRLF would put a '\r' into the query.
            File.WriteAllText(Path.Combine(outDir, promptFile), template.Replace("\r\n", "\n").Replace("{query}", query), new UTF8Encoding(false));
            var cwd = t.Class == "C" ? corpus!.Path : workspace;
            planTasks.Add(new PlanTask(t.Id!, t.Class!, t.Corpus!, cwd, promptFile));
            tsv.Append(t.Id).Append('\t').Append(t.Class).Append('\t').Append(t.Corpus).Append('\t').Append(promptFile).Append('\n');
        }
        File.WriteAllText(Path.Combine(outDir, "plan.tsv"), tsv.ToString(), new UTF8Encoding(false));

        var plan = new Plan(1, tasksPath, workspace, brain.Name, brain.DatabasePath, corpus?.Path, corpus?.DatabasePath, mappingPath, o.ClassFilter, planTasks);
        File.WriteAllText(Path.Combine(outDir, PlanFile), JsonSerializer.Serialize(plan, JsonOptions));
        // KEY=VALUE for run.sh — bash has no JSON; forward slashes so the same value works in an MCP config.
        var env = new StringBuilder()
            .Append("BRAIN_ROOT=").Append(Fwd(workspace)).Append('\n')
            .Append("BRAIN_PROJECT=").Append(brain.Name).Append('\n')
            .Append("BRAIN_DB=").Append(Fwd(brain.DatabasePath)).Append('\n')
            .Append("CORPUS_ROOT=").Append(Fwd(corpus?.Path ?? string.Empty)).Append('\n')
            .Append("CORPUS_PROJECT=").Append(CorpusProjectName).Append('\n')
            .Append("CORPUS_DB=").Append(Fwd(corpus?.DatabasePath ?? string.Empty)).Append('\n')
            .Append("CORPUS_REVISION=").Append(mapping?.CorpusRevision ?? string.Empty).Append('\n')
            .Append("MAPPING=").Append(Fwd(mappingPath ?? string.Empty)).Append('\n');
        File.WriteAllText(Path.Combine(outDir, "plan.env"), env.ToString(), new UTF8Encoding(false));

        console.WriteLine($"Plan: {planTasks.Count} task(s) — A {planTasks.Count(t => t.Class == "A")}, B {planTasks.Count(t => t.Class == "B")}, C {planTasks.Count(t => t.Class == "C")}; class C tokens resolved: {tokensResolved}.");
        console.WriteLine($"Brain '{brain.Name}' at {Short(world.BrainHead)} (graph {Short(world.BrainIndexedRevision)}){(corpus is null ? "" : $"; corpus at {Short(world.CorpusHead)} (graph {Short(world.CorpusIndexedRevision)}, mapping {Short(mapping?.CorpusRevision)})")}.");
        console.WriteLine($"Wrote {Path.Combine(outDir, "plan.tsv")}, plan.env, {PlanFile} and {planTasks.Count} prompt file(s).");
        return 0;
    }

    /// <summary>
    /// <c>--out</c> as an absolute path. An MSYS drive path (<c>/c/Projects/x</c>, what a Git-Bash caller may
    /// pass through) is read as <c>C:/Projects/x</c> on Windows — <see cref="Path.GetFullPath(string)"/> would
    /// otherwise root it on the current drive and the containment check below would look at the wrong place.
    /// </summary>
    internal static string ResolveOutDir(string outDir)
    {
        if (OperatingSystem.IsWindows() && Regex.Match(outDir, @"^/([A-Za-z])(/|$)") is { Success: true } m)
            outDir = $"{char.ToUpperInvariant(m.Groups[1].Value[0])}:/{outDir[m.Length..]}";
        return Path.GetFullPath(outDir);
    }

    /// <summary>
    /// Why <paramref name="outDir"/> must not be the run directory, or <c>null</c> when it may: it is the Brain
    /// checkout or the corpus checkout, or lies inside either. Same rule as <c>run.sh</c> — this is the copy
    /// that holds when the bench is called directly.
    /// </summary>
    internal static string? OutDirProblem(string outDir, string brainRoot, string? corpusRoot)
    {
        var full = Path.GetFullPath(outDir);
        if (IsInside(full, brainRoot)) return $"run directory '{full}' lies inside the Brain repository ('{Path.GetFullPath(brainRoot)}').";
        // The corpus root is a customer path: the message shows the run directory relative to <corpus>, never the root itself.
        if (!string.IsNullOrEmpty(corpusRoot) && IsInside(full, corpusRoot))
            return $"run directory '<corpus>/{(FilePaths.TryGetRelative(full, corpusRoot, out var rel) ? rel.Replace('\\', '/') : string.Empty)}' lies inside the corpus repository.";
        return null;

        static bool IsInside(string path, string root)
        {
            var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var p = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return FilePaths.AreEqual(p, r) || FilePaths.TryGetRelative(p, r, out _);
        }
    }

    // ---------- --ap6-tally ----------

    /// <summary>
    /// Σ <c>total_cost_usd</c> over every stream so far, writing each run's <c>result.json</c> (its last result event)
    /// beside it. The driver reads the one line this prints: <c>redo=[…]</c> names the runs (<c>task/arm/n</c>) that
    /// are no measurement (<see cref="Ap6Scorer.NotRunReason"/>: limit hit, API error, no result event) and are run
    /// again on <c>--resume</c>; <c>limit=[…]</c> is the subset that hit a usage/rate limit — the driver stops the set on it.
    /// </summary>
    public static int Tally(string runDir, TextWriter console)
    {
        var dir = Path.GetFullPath(runDir);
        if (!Directory.Exists(dir)) { console.WriteLine($"[Error] run directory not found at '{dir}'."); return 1; }
        double cost = 0; int runs = 0, withoutResult = 0;
        var redo = new List<string>(); var limit = new List<string>();
        foreach (var stream in Directory.EnumerateFiles(dir, "stream.jsonl", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            runs++;
            var text = File.ReadAllText(stream);
            var record = Ap6RunReader.Read(text);
            var reason = Ap6Scorer.NotRunReason(record);
            if (reason is not null)
            {
                var key = Fwd(Path.GetRelativePath(dir, Path.GetDirectoryName(stream)!));
                redo.Add(key);
                if (Ap6Scorer.IsLimitReason(reason)) limit.Add(key);
            }
            if (!record.SawResult) { withoutResult++; continue; }
            cost += record.CostUsd;
            var resultPath = Path.Combine(Path.GetDirectoryName(stream)!, "result.json");
            if (!File.Exists(resultPath))
            {
                var last = text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Contains("\"type\":\"result\"", StringComparison.Ordinal) || l.Contains("\"type\": \"result\"", StringComparison.Ordinal));
                if (last is not null) File.WriteAllText(resultPath, last + "\n");
            }
        }
        console.WriteLine($"runs={runs} cost_usd={cost.ToString("0.0000", CultureInfo.InvariantCulture)} without_result={withoutResult} redo=[{string.Join(",", redo)}] limit=[{string.Join(",", limit)}]");
        return 0;
    }

    // ---------- --ap6 ----------

    public static async Task<int> ScoreAsync(SqliteGraphStorageProvider brainProvider, ScoreOptions o, TextWriter console)
    {
        var runDir = Path.GetFullPath(o.RunDir);
        var planPath = Path.Combine(runDir, PlanFile);
        if (!File.Exists(planPath)) { console.WriteLine($"[Error] '{planPath}' not found — the run directory was not produced by --ap6-plan. Nothing scored."); return 0; }
        // Corrupt JSON anywhere in the run directory is a finding about the run, not a crash: say so and exit 0 like every other "nothing scored".
        Plan? plan;
        try { plan = JsonSerializer.Deserialize<Plan>(File.ReadAllText(planPath), JsonOptions); }
        catch (JsonException ex) { console.WriteLine($"[Error] '{planPath}' is not valid JSON ({ex.Message}). Nothing scored."); return 0; }
        if (plan is null || plan.Tasks is null || !File.Exists(plan.TasksPath)) { console.WriteLine($"[Error] plan.json unreadable or its tasks file is gone ('{plan?.TasksPath}'). Nothing scored."); return 0; }

        var notes = new List<string>();
        var planned = plan.Tasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        List<Ap6Task> tasks;
        try { tasks = Ap6Corpus.Load(plan.TasksPath).Where(t => t.Id is not null && planned.Contains(t.Id)).ToList(); }
        catch (JsonException ex) { console.WriteLine($"[Error] tasks file '{plan.TasksPath}' is not valid JSON ({ex.Message}). Nothing scored."); return 0; }

        Ap6Mapping? mapping = null;
        if (tasks.Any(t => t.Class == "C"))
        {
            if (o.CorpusDbPath is null)
            {
                notes.Add("Class C skipped: no --db-c <corpus.db> given — its graph state cannot be recorded, so its runs are not scored.");
                tasks = tasks.Where(t => t.Class != "C").ToList();
            }
            else if (plan.MappingPath is null || !File.Exists(plan.MappingPath))
            {
                notes.Add("Class C skipped: the mapping file recorded in plan.json is not present — its answers cannot be read back into tokens.");
                tasks = tasks.Where(t => t.Class != "C").ToList();
            }
            else
            {
                try { mapping = Ap6Mapping.Load(plan.MappingPath); }
                catch (JsonException ex)
                {
                    notes.Add($"Class C skipped: the mapping file is not valid JSON ({ex.Message}) — its answers cannot be read back into tokens.");
                    tasks = tasks.Where(t => t.Class != "C").ToList();
                }
            }
        }

        var envPath = Path.Combine(runDir, EnvFile);
        var env = Ap6Env.Empty;
        if (!File.Exists(envPath)) notes.Add("env.json missing from the run directory — versions, limits and plugin verify output are not recorded.");
        else
        {
            try { env = Ap6Env.Parse(File.ReadAllText(envPath)); }
            catch (JsonException ex) { notes.Add($"env.json is not valid JSON ({ex.Message}) — versions, limits and plugin verify output are not recorded."); }
        }
        env = env with
        {
            PluginVerifyOutput = env.PluginVerifyOutput is null ? null : mapping is null ? Ap6Anonymiser.RedactPaths(env.PluginVerifyOutput) : Ap6Anonymiser.RedactArgument(env.PluginVerifyOutput, mapping),
            ShonkorExe = env.ShonkorExe is null ? null : Ap6Anonymiser.RedactPaths(env.ShonkorExe),
        };

        var verdicts = new List<Ap6RunVerdict>();
        var toolCalls = new Dictionary<(string, string, int), List<Ap6ToolCall>>();
        var missing = new List<string>();
        var mcpTools = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            var cwd = plan.Tasks.First(p => p.Id == task.Id).Cwd;
            foreach (var arm in Ap6Scorer.Arms)
            for (var run = 1; run <= Ap6Scorer.RunsPerArm; run++)
            {
                var dir = Path.Combine(runDir, task.Id!, arm, run.ToString(CultureInfo.InvariantCulture));
                var streamPath = Path.Combine(dir, "stream.jsonl");
                if (!File.Exists(streamPath)) { missing.Add($"{task.Id}/{arm}/{run}"); continue; }
                var record = Ap6RunReader.Read(File.ReadAllText(streamPath));
                var verdict = Ap6Scorer.Score(task, arm, run, record, o.Mode, cwd, mapping);
                verdicts.Add(verdict);
                if (arm == Ap6Scorer.McpArm) foreach (var t in record.InitTools.Where(t => t.StartsWith(Ap6Scorer.McpToolPrefix, StringComparison.Ordinal))) mcpTools.Add(t);
                // Every class's inputs are redacted before they can reach a results file. Class C is rebuilt
                // string by string (#511): a class-C input is customer text unless it proves otherwise, so the
                // default is <redacted> and only tokens, placeholders and allow-listed vocabulary survive.
                // Class A/B is Brain — our own code — and only needs paths under the arm's cwd made relative
                // (the rg arm reads by absolute path, which is a FindLeaks pattern; without this no results
                // file could ever be written).
                if (task.Class == "C" && mapping is not null)
                {
                    // Names as well as inputs (#514): a name is the model's text too, and it used to be
                    // written raw and checked by nobody.
                    var (redactedCalls, n) = Ap6Anonymiser.RedactCalls(record.ToolCalls, record.InitTools, mapping);
                    verdict.RedactedStrings += n;
                    toolCalls[(task.Id!, arm, run)] = redactedCalls;
                }
                else
                {
                    toolCalls[(task.Id!, arm, run)] = record.ToolCalls.Select(c => new Ap6ToolCall(c.Name, Ap6Anonymiser.RelativiseArgument(c.Input, cwd))).ToList();
                }
                File.WriteAllText(Path.Combine(dir, "meta.json"), JsonSerializer.Serialize(new { record, verdict }, JsonOptions));
            }
        }

        var graphs = new List<Ap6GraphState> { await GraphStateAsync("Brain", brainProvider).ConfigureAwait(false) };
        if (o.CorpusDbPath is not null && tasks.Any(t => t.Class == "C"))
        {
            if (!File.Exists(o.CorpusDbPath)) graphs.Add(new Ap6GraphState(CorpusProjectName, null, null, 0, 0, 0, [], $"database not found at the --db-c path"));
            else
            {
                using var corpusProvider = new SqliteGraphStorageProvider(o.CorpusDbPath);
                await corpusProvider.InitializeAsync().ConfigureAwait(false);
                graphs.Add(await GraphStateAsync(CorpusProjectName, corpusProvider).ConfigureAwait(false));
            }
        }
        // The graph scored against is read now; the run happened at env.json's revisions. A difference means the
        // numbers describe one graph and the runs another — said in the report, not silently averaged away.
        foreach (var drift in GraphDriftNotes(graphs, env)) notes.Add(drift);

        var data = new Ap6ReportData
        {
            RunDirName = Path.GetFileName(runDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            GeneratedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture),
            MatchMode = o.Mode,
            Env = env,
            Tasks = tasks,
            Verdicts = verdicts,
            Outcomes = Ap6Scorer.Outcomes(tasks, verdicts),
            Graphs = graphs,
            McpToolsOffered = mcpTools.ToList(),
            MissingRuns = missing,
            Notes = notes,
            ToolCalls = toolCalls,
        };

        // Every artefact is rendered and checked BEFORE any of them is written (#514). The old order wrote
        // the report first and only then checked results-C.json, so a class-C string layer 1 did not get
        // clean could land in bench/ap6-part1-report.md — in the repository, in the history — while the
        // results file it came from was refused. The publication is all-or-nothing: one leak anywhere and
        // nothing is written, because the two files carry the same strings.
        var denyHashes = mapping?.DenyWordHashes();
        var markdown = Ap6Report.Markdown(data);
        var artefacts = new List<(string Path, string Content, IReadOnlyList<string> Leaks)>
        {
            (Path.Combine(plan.BrainRoot, ReportRelativePath.Replace('/', Path.DirectorySeparatorChar)),
                markdown, Ap6Corpus.FindLeaks(markdown, denyHashes)),
        };
        foreach (var cls in Ap6Report.Classes.Where(c => tasks.Any(t => t.Class == c)))
        {
            var json = Ap6Report.ResultsJson(data, cls);
            artefacts.Add((
                Path.Combine(plan.BrainRoot, ResultsRelativeDir.Replace('/', Path.DirectorySeparatorChar), $"results-{cls}.json"),
                json,
                Ap6Corpus.FindResultsLeaks(json, cls, denyHashes)));
        }

        if (artefacts.Any(a => a.Leaks.Count > 0))
        {
            foreach (var (path, _, leaks) in artefacts.Where(a => a.Leaks.Count > 0))
                console.WriteLine($"[Error] {Path.GetFileName(path)}: {leaks.Count} leak pattern(s) matched: {string.Join("; ", leaks.Take(5))}");
            console.WriteLine($"[Error] NOTHING written — {artefacts.Count} artefact(s) are checked together and published together. The run directory keeps every number; fix the redaction and re-score with --ap6 (no run is repeated).");
        }
        else
        {
            foreach (var (path, content, _) in artefacts)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content, new UTF8Encoding(false));
                console.WriteLine($"Wrote {path}");
            }
        }

        foreach (var n in notes) console.WriteLine($"NOTE: {n}");
        foreach (var cls in Ap6Report.Classes)
        {
            var outcomes = data.Outcomes.Where(x => x.Class == cls).ToList();
            if (outcomes.Count == 0) continue;
            console.WriteLine($"Class {cls}: {outcomes.Count} task(s); mcp majority-correct {outcomes.Count(x => x.Mcp.Majority == Ap6Majority.Correct)}, rg {outcomes.Count(x => x.Rg.Majority == Ap6Majority.Correct)}, incomplete {outcomes.Count(x => x.Mcp.Majority == Ap6Majority.Incomplete || x.Rg.Majority == Ap6Majority.Incomplete)}; arm violations {verdicts.Count(v => v.Class == cls && !v.Scored)}; missing runs {missing.Count(m => tasks.Any(t => t.Class == cls && t.Id == m.Split('/')[0]))}");
            if (cls == "C") console.WriteLine($"Gate: {Ap6Scorer.Gate(outcomes).Decision}");
        }
        // A lens, not a gate: whatever the numbers say, reading them is the point.
        return 0;
    }

    /// <summary>One note per graph whose <c>indexedRevision</c> at scoring time is not the revision <c>env.json</c> recorded for the run.</summary>
    internal static IEnumerable<string> GraphDriftNotes(IReadOnlyList<Ap6GraphState> graphs, Ap6Env env)
    {
        foreach (var g in graphs)
        {
            var (expected, source) = g.Name == CorpusProjectName ? (env.CorpusRevision, "env.corpusRevision") : (env.BrainHead, "env.brainHead");
            if (string.IsNullOrEmpty(expected) || g.IndexedRevision is null) continue;
            if (!expected.StartsWith(g.IndexedRevision, StringComparison.OrdinalIgnoreCase) && !g.IndexedRevision.StartsWith(expected, StringComparison.OrdinalIgnoreCase))
                yield return $"Graph drift: {g.Name} graph scored at indexedRevision {Short(g.IndexedRevision)}, but the run was recorded at {source} {Short(expected)} — the graph changed between run and score; the graph-state table describes the scoring-time graph.";
        }
    }

    private static async Task<Ap6GraphState> GraphStateAsync(string name, SqliteGraphStorageProvider provider)
    {
        var stats = await provider.GetStatisticsAsync().ConfigureAwait(false);
        var edges = await provider.GetAllEdgesAsync().ConfigureAwait(false);
        var byReason = edges.GroupBy(e => e.Reason.ToString(), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return new Ap6GraphState(
            name,
            await provider.GetIndexedRevisionAsync().ConfigureAwait(false),
            await provider.GetToolchainFingerprintAsync().ConfigureAwait(false),
            await provider.GetNodeIdSchemeVersionAsync().ConfigureAwait(false),
            (int)stats.TotalNodes, (int)stats.TotalEdges, byReason,
            byReason.TryGetValue("Unspecified", out var u) && u > 0 ? $"{u} edge(s) carry Reason=Unspecified — a producer that has not declared its reason (#428 gap)." : null);
    }

    private static async Task<string?> IndexedRevisionAsync(string? dbPath)
    {
        if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath)) return null;
        using var provider = new SqliteGraphStorageProvider(dbPath);
        await provider.InitializeAsync().ConfigureAwait(false);
        return await provider.GetIndexedRevisionAsync().ConfigureAwait(false);
    }

    private static string Fwd(string p) => p.Replace('\\', '/');
    private static string Short(string? sha) => sha is null ? "?" : sha.Length > 12 ? sha[..12] : sha;

    private static string? GitHead(string dir) => Git(dir, "rev-parse HEAD")?.Trim() is { Length: > 0 } head ? head : null;

    /// <summary>Tracked-modified and untracked files with <paramref name="extension"/> — as <c>LspDiffRunner</c> guards the LSP diff.</summary>
    private static List<string> GitDirtyFiles(string dir, string extension) =>
        (Git(dir, "status --porcelain --untracked-files=all") ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Length > 3 ? l[3..].Trim() : string.Empty)
            .Where(f => f.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>Tracked files of the given kinds that differ from HEAD — the check <c>keys-c.sh</c> makes before keying.</summary>
    private static List<string> GitModifiedTracked(string dir, params string[] globs) =>
        (Git(dir, "diff --name-only HEAD -- " + string.Join(' ', globs.Select(g => $"\"{g}\""))) ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    private static string? Git(string dir, string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", $"-C \"{dir}\" {arguments}")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            });
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
