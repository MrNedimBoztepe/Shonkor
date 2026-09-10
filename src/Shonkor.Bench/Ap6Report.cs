// Licensed to Shonkor under the MIT License.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shonkor.Bench;

/// <summary>What <c>run.sh</c> recorded in <c>env.json</c>. Every field optional — a missing one prints as <c>?</c>, never as a default.</summary>
internal sealed record Ap6Env(
    string? GeneratedAt,
    string? ClaudeVersion,
    string? MinClaudeVersion,
    string? RgVersion,
    string? Model,
    string? Effort,
    int? MaxTurns,
    double? MaxUsdPerRun,
    double? RunSetUsdCap,
    int? RunsPerArm,
    bool? Smoke,
    string? ShonkorExe,
    string? ShonkorDllSha256,
    string? BrainHead,
    string? CorpusHead,
    string? CorpusRevision,
    int? PluginVerifyExit,
    string? PluginVerifyOutput,
    Dictionary<string, string>? EnvFlags,
    string? AuthMode,
    string? IsolationFlags,
    /// <summary>The <c>--tools</c> / <c>--allowedTools</c> / <c>--disallowedTools</c> of both arms (#513) — what each arm was allowed to reach for.</summary>
    string? PermissionRules,
    /// <summary>The PreToolUse hooks each arm ran under (#513) — what actually refused a call, as opposed to what merely failed to pre-approve it.</summary>
    string? Hooks)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    public static Ap6Env Parse(string json) => JsonSerializer.Deserialize<Ap6Env>(json, JsonOptions) ?? Empty;
    public static readonly Ap6Env Empty = new(null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);
}

/// <summary>One graph's identity at scoring time — the Meta stamps and the edge census per <c>ProvenanceReason</c>.</summary>
internal sealed record Ap6GraphState(
    string Name,
    string? IndexedRevision,
    string? ToolchainFingerprint,
    int SchemeVersion,
    int Nodes,
    int Edges,
    Dictionary<string, int> EdgesByReason,
    string? Note);

/// <summary>Everything one <c>--ap6</c> scoring pass produced, for the renderer.</summary>
internal sealed class Ap6ReportData
{
    public string RunDirName { get; init; } = string.Empty;
    public string GeneratedAt { get; init; } = string.Empty;
    public Ap6MatchMode MatchMode { get; init; }
    public Ap6Env Env { get; init; } = Ap6Env.Empty;
    public List<Ap6Task> Tasks { get; init; } = [];
    public List<Ap6RunVerdict> Verdicts { get; init; } = [];
    public List<Ap6TaskOutcome> Outcomes { get; init; } = [];
    public List<Ap6GraphState> Graphs { get; init; } = [];
    /// <summary>The MCP arm's <c>init.tools</c>, union over its runs — the recorded <c>tools/list</c> for #474.</summary>
    public List<string> McpToolsOffered { get; init; } = [];
    /// <summary>(task, arm, run) triples without a stream — never scored, shown so an incomplete majority is explainable.</summary>
    public List<string> MissingRuns { get; init; } = [];
    public List<string> Notes { get; init; } = [];
    /// <summary>Class C tool-call inputs, redacted; class A/B verbatim. Keyed by (task, arm, run).</summary>
    public Dictionary<(string Task, string Arm, int Run), List<Ap6ToolCall>> ToolCalls { get; init; } = [];
}

/// <summary>
/// Renders the per-class report (<c>bench/ap6-part1-report.md</c>) and the pinned <c>results-&lt;class&gt;.json</c>.
/// One table per class, every run its own row, no mean, no median, no aggregate across classes — the
/// policy (<c>bench/golden/ap6-precision-evidence-policy.md</c>) forbids the readings such an aggregate invites.
/// </summary>
internal static class Ap6Report
{
    public static readonly string[] Classes = ["A", "B", "C"];

    /// <summary>The open graph defects that bound every number here — listed, not hidden, on every report.</summary>
    public static readonly (int Issue, string Title)[] OpenDefects =
    [
        (429, "mcp: implementations_of shows no trust tier, and since #402 every row it prints is a name guess"),
        (405, "graph: syntactic IMPLEMENTS/EXTENDS carry bare type names — 1 377 duplicate a resolved edge, 303 are real gaps"),
        (440, "IMPORTS: 2 839 of 4 371 edges point at module specifiers that are not nodes"),
        (436, "3 318 Extracted edges point at ids inside the indexed tree that no node carries"),
        (463, "FromHandle normalizes a handle's relative part but not the base path, so a project registered with '/' cannot resolve its own handles"),
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---------- markdown ----------

    public static string Markdown(Ap6ReportData d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# AP6 part 1 — two-arm harness report (#473)");
        sb.AppendLine();
        sb.AppendLine($"Generated {d.GeneratedAt} from run directory `{d.RunDirName}` by `shonkor-bench --ap6`. Match mode: `{d.MatchMode}` (default `{Ap6Scorer.DefaultMatchMode}`; `--ap6-match exact` is the alternative). Every run is its own row; there is no mean, no median and no aggregate across classes.");
        sb.AppendLine();
        foreach (var n in d.Notes) sb.AppendLine($"> {n}");
        if (d.Notes.Count > 0) sb.AppendLine();

        sb.AppendLine("## Environment");
        sb.AppendLine();
        sb.AppendLine("| Item | Value |");
        sb.AppendLine("|---|---|");
        var e = d.Env;
        sb.AppendLine($"| claude | `{e.ClaudeVersion ?? "?"}` (required ≥ {e.MinClaudeVersion ?? "?"}) |");
        sb.AppendLine($"| rg | `{e.RgVersion ?? "?"}` |");
        sb.AppendLine($"| model | `{e.Model ?? "?"}` |");
        sb.AppendLine($"| effort | `{e.Effort ?? "?"}` |");
        sb.AppendLine($"| auth mode | `{e.AuthMode ?? "?"}` |");
        sb.AppendLine($"| isolation flags | `{e.IsolationFlags ?? "?"}` |");
        sb.AppendLine($"| permission rules | `{e.PermissionRules ?? "?"}` |");
        sb.AppendLine($"| hooks | `{e.Hooks ?? "?"}` |");
        sb.AppendLine($"| shonkor.dll SHA-256 | `{e.ShonkorDllSha256 ?? "?"}` |");
        sb.AppendLine($"| Brain HEAD | `{e.BrainHead ?? "?"}` |");
        sb.AppendLine($"| corpus HEAD | `{e.CorpusHead ?? "?"}` |");
        sb.AppendLine($"| mapping corpusRevision | `{e.CorpusRevision ?? "?"}` |");
        sb.AppendLine($"| plugin verify exit | {(e.PluginVerifyExit?.ToString(Inv) ?? "?")} |");
        sb.AppendLine($"| smoke | {(e.Smoke is null ? "?" : e.Smoke.Value ? "yes" : "no")} |");
        foreach (var (k, v) in e.EnvFlags ?? []) sb.AppendLine($"| env {k} | `{v}` |");
        sb.AppendLine();
        sb.AppendLine("`shonkor plugin verify` output:");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine((e.PluginVerifyOutput ?? "(not recorded)").TrimEnd());
        sb.AppendLine("```");
        sb.AppendLine();

        sb.AppendLine("## Graph state");
        sb.AppendLine();
        foreach (var g in d.Graphs)
        {
            sb.AppendLine($"### {g.Name}");
            sb.AppendLine();
            sb.AppendLine("| indexedRevision | toolchainFingerprint | scheme version | nodes | edges |");
            sb.AppendLine("|---|---|---:|---:|---:|");
            sb.AppendLine($"| `{g.IndexedRevision ?? "(none)"}` | `{g.ToolchainFingerprint ?? "(none)"}` | {g.SchemeVersion} | {g.Nodes} | {g.Edges} |");
            sb.AppendLine();
            sb.AppendLine("| ProvenanceReason | edges |");
            sb.AppendLine("|---|---:|");
            foreach (var (reason, n) in g.EdgesByReason.OrderBy(kv => kv.Key, StringComparer.Ordinal)) sb.AppendLine($"| {reason} | {n} |");
            if (g.Note is not null) { sb.AppendLine(); sb.AppendLine($"> {g.Note}"); }
            sb.AppendLine();
        }

        sb.AppendLine("## Open graph defects bounding these numbers");
        sb.AppendLine();
        foreach (var (issue, title) in OpenDefects) sb.AppendLine($"- #{issue} — {title}");
        sb.AppendLine();

        foreach (var cls in Classes) AppendClass(sb, d, cls);

        sb.AppendLine("## MCP tools offered (recorded `tools/list`, input for #474)");
        sb.AppendLine();
        if (d.McpToolsOffered.Count == 0) sb.AppendLine("(no MCP-arm run with a system/init event)");
        foreach (var t in d.McpToolsOffered) sb.AppendLine($"- `{t}`");
        sb.AppendLine();
        return sb.ToString();
    }

    private static void AppendClass(StringBuilder sb, Ap6ReportData d, string cls)
    {
        var tasks = d.Tasks.Where(t => t.Class == cls).ToList();
        var e = d.Env;
        var (corpus, expectation) = cls switch { "A" => ("Brain", "rg"), "B" => ("Brain", "graph"), _ => ("Corpus-A", "graph-only") };
        sb.AppendLine($"## Class {cls} — {corpus} (expectation: {expectation})");
        sb.AppendLine();
        sb.AppendLine(LimitsLine(e, d.MatchMode));
        sb.AppendLine();
        if (tasks.Count == 0)
        {
            sb.AppendLine("(not scored in this run — see notes above)");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| Task | Arm | Run | Correct | overSelect | toolCalls | tokensApprox | usageExact | cost USD | turns | bashNonRg | mcpOverflow | flags |");
        sb.AppendLine("|---|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var t in tasks)
        foreach (var arm in Ap6Scorer.Arms)
        foreach (var v in d.Verdicts.Where(v => v.TaskId == t.Id && v.Arm == arm).OrderBy(v => v.Run))
        {
            sb.AppendLine($"| {v.TaskId} | {v.Arm} | {v.Run} | {(v.Scored ? (v.Correct ? "yes" : "no") : "—")} | {v.OverSelect} | {v.ToolCalls} | {v.TokensApprox} | {v.UsageExact} | {v.CostUsd.ToString("0.0000", Inv)} | {v.Turns} | {v.BashNonRg} | {v.McpOverflow} | {Flags(v)} |");
        }
        sb.AppendLine();

        sb.AppendLine($"**Majority** (correct in ≥ 2 of {Ap6Scorer.RunsPerArm} scored runs; fewer than {Ap6Scorer.RunsPerArm} scored runs is `incomplete`)");
        sb.AppendLine();
        sb.AppendLine("| Task | mcp | rg |");
        sb.AppendLine("|---|---|---|");
        foreach (var o in d.Outcomes.Where(o => o.Class == cls))
            sb.AppendLine($"| {o.TaskId} | {Majority(o.Mcp)} | {Majority(o.Rg)} |");
        sb.AppendLine();

        var violations = d.Verdicts.Where(v => v.Class == cls && v.ArmViolation is not null).ToList();
        sb.AppendLine(violations.Count == 0
            ? "Arm violations: none."
            : "Arm violations (listed, not counted): " + string.Join("; ", violations.Select(v => $"{v.TaskId}/{v.Arm}/{v.Run}: {v.ArmViolation}")));
        var notRun = d.Verdicts.Where(v => v.Class == cls && v.NotRun).ToList();
        if (notRun.Count > 0)
            sb.AppendLine("Not run (usage/rate limit or API error — listed, not counted, re-run with `run.sh <run-dir> --resume`): " + string.Join("; ", notRun.Select(v => $"{v.TaskId}/{v.Arm}/{v.Run}: {v.NotRunReason}")));
        var missing = d.MissingRuns.Where(m => tasks.Any(t => t.Id == m.Split('/')[0])).ToList();
        if (missing.Count > 0) sb.AppendLine($"Missing runs (no stream.jsonl): {string.Join(", ", missing)}.");
        sb.AppendLine();

        sb.AppendLine("Tool calls by name:");
        sb.AppendLine();
        sb.AppendLine("| Arm | Tool | calls |");
        sb.AppendLine("|---|---|---:|");
        foreach (var arm in Ap6Scorer.Arms)
        {
            var counts = d.ToolCalls.Where(kv => kv.Key.Arm == arm && tasks.Any(t => t.Id == kv.Key.Task))
                .SelectMany(kv => kv.Value).GroupBy(c => c.Name, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal);
            foreach (var g in counts) sb.AppendLine($"| {arm} | `{g.Key}` | {g.Count()} |");
        }
        sb.AppendLine();

        if (cls == "C")
        {
            var gate = Ap6Scorer.Gate(d.Outcomes.Where(o => o.Class == "C").ToList());
            sb.AppendLine($"**Gate.** {Ap6Scorer.GateText}");
            sb.AppendLine();
            sb.AppendLine($"Arithmetic: {gate.Arithmetic}");
            sb.AppendLine();
            sb.AppendLine($"Decision: {gate.Decision}");
            sb.AppendLine();
        }
    }

    /// <summary>
    /// The limits and the arm-purity measures, printed beside every class table: a reader must not have to
    /// take on trust that the rg arm was ripgrep-only (#513), so the rules and the hook stand next to the
    /// numbers they bound. <c>toolCalls</c> is said out loud too, because #512 changed what it counts.
    /// </summary>
    public static string LimitsLine(Ap6Env e, Ap6MatchMode mode) =>
        $"Model `{e.Model ?? "?"}`, effort `{e.Effort ?? "?"}`, max turns {(e.MaxTurns?.ToString(Inv) ?? "?")}, max USD per run {(e.MaxUsdPerRun?.ToString("0.00", Inv) ?? "?")}, run-set USD cap {(e.RunSetUsdCap?.ToString("0.00", Inv) ?? "?")}, runs per arm {(e.RunsPerArm?.ToString(Inv) ?? "?")}, match mode `{mode}`."
        + $" Permission rules: {e.PermissionRules ?? "?"}. Hooks: {e.Hooks ?? "?"}."
        + $" `toolCalls` counts research steps only — the `{Ap6Scorer.AnswerTool}` emission is the answer channel, not a step, and is excluded in both arms."
        + " It does include calls the hook REFUSED (`bashNonRgDenied`): the arm spent the turn either way, so the tool-economy figure of the rg arm carries the cost of its own guard.";

    private static string Majority(Ap6ArmOutcome o) =>
        $"{o.Majority.ToString().ToLowerInvariant()} ({o.CorrectRuns}/{o.ScoredRuns})";

    private static string Flags(Ap6RunVerdict v)
    {
        var flags = new List<string>();
        if (v.ArmViolation is not null) flags.Add($"armViolation: {v.ArmViolation}");
        if (v.NotRun) flags.Add($"notRun: {v.NotRunReason}");
        if (v.NoAnswer) flags.Add("noAnswer");
        if (v.IsError) flags.Add("isError");
        if (v.MissingFiles > 0) flags.Add($"missingFiles {v.MissingFiles}");
        if (v.MissingSymbols > 0) flags.Add($"missingSymbols {v.MissingSymbols}");
        if (v.AmbiguousSymbols > 0) flags.Add($"ambiguous {v.AmbiguousSymbols}");
        if (v.UnmappedFiles > 0) flags.Add($"unmappedFiles {v.UnmappedFiles}");
        if (v.UnmappedSymbols > 0) flags.Add($"unmappedSymbols {v.UnmappedSymbols}");
        if (v.RedactedStrings > 0) flags.Add($"redactedStrings {v.RedactedStrings}");
        if (v.BashNonRgDenied > 0) flags.Add($"bashNonRgDenied {v.BashNonRgDenied}");
        if (v.PermissionDenials > 0) flags.Add($"permissionDenials {v.PermissionDenials}");
        return string.Join(", ", flags);
    }

    // ---------- results-<class>.json ----------

    public static string ResultsJson(Ap6ReportData d, string cls)
    {
        var tasks = d.Tasks.Where(t => t.Class == cls).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var e = d.Env;
        var payload = new
        {
            // 2 (#511/#512/#513): the added fields are additive, but `toolCallCount` changed MEANING — it no
            // longer counts the StructuredOutput answer emission, so a v1 and a v2 number are not comparable
            // even though the field name and type are the same. That is what the version bump is for; a
            // silently-additive change would have left every pinned v1 figure one too high and readable as if
            // it were not. `bashNonRg` narrowed the same way: denied attempts moved to `bashNonRgDenied`.
            schemaVersion = 2,
            @class = cls,
            matchMode = d.MatchMode.ToString(),
            runDir = d.RunDirName,
            generatedAt = d.GeneratedAt,
            limits = new { model = e.Model, effort = e.Effort, maxTurns = e.MaxTurns, maxUsdPerRun = e.MaxUsdPerRun, runSetUsdCap = e.RunSetUsdCap, runsPerArm = e.RunsPerArm },
            authMode = e.AuthMode,
            isolationFlags = e.IsolationFlags,
            permissionRules = e.PermissionRules,
            hooks = e.Hooks,
            versions = new { claude = e.ClaudeVersion, rg = e.RgVersion, shonkorDllSha256 = e.ShonkorDllSha256, brainHead = e.BrainHead, corpusHead = e.CorpusHead, corpusRevision = e.CorpusRevision },
            graph = d.Graphs.FirstOrDefault(g => g.Name == (cls == "C" ? "Corpus-A" : "Brain")),
            mcpToolsOffered = d.McpToolsOffered,
            runs = d.Verdicts.Where(v => tasks.Contains(v.TaskId)).OrderBy(v => v.TaskId, StringComparer.Ordinal).ThenBy(v => v.Arm).ThenBy(v => v.Run)
                .Select(v => new
                {
                    task = v.TaskId, arm = v.Arm, run = v.Run,
                    scored = v.Scored, armViolation = v.ArmViolation, notRun = v.NotRun, notRunReason = v.NotRunReason,
                    correct = v.Correct, noAnswer = v.NoAnswer, overSelect = v.OverSelect,
                    missingFiles = v.MissingFiles, missingSymbols = v.MissingSymbols, ambiguousSymbols = v.AmbiguousSymbols,
                    unmappedFiles = v.UnmappedFiles, unmappedSymbols = v.UnmappedSymbols, redactedStrings = v.RedactedStrings,
                    // Research steps only — the answer emission is not one (#512, see schemaVersion).
                    toolCallCount = v.ToolCalls, tokensApprox = v.TokensApprox, usageExact = v.UsageExact,
                    costUsd = v.CostUsd, durationMs = v.DurationMs, turns = v.Turns,
                    bashNonRg = v.BashNonRg, bashNonRgDenied = v.BashNonRgDenied,
                    mcpOverflow = v.McpOverflow, permissionDenials = v.PermissionDenials, isError = v.IsError,
                    answerFiles = v.AnswerFiles, answerSymbols = v.AnswerSymbols,
                    toolCalls = d.ToolCalls.TryGetValue((v.TaskId, v.Arm, v.Run), out var calls) ? calls.Select(c => new { name = c.Name, input = c.Input }).ToList() : [],
                }).ToList(),
            majority = d.Outcomes.Where(o => o.Class == cls).Select(o => new { task = o.TaskId, mcp = o.Mcp, rg = o.Rg }).ToList(),
            missingRuns = d.MissingRuns.Where(m => m.Split('/')[0] is { } id && tasks.Contains(id)).ToList(),
            gate = cls == "C" ? Ap6Scorer.Gate(d.Outcomes.Where(o => o.Class == "C").ToList()) : null,
            gateText = cls == "C" ? Ap6Scorer.GateText : null,
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }
}
