// Licensed to Shonkor under the MIT License.

using System.Globalization;
using System.Text.RegularExpressions;
using Shonkor.Core.Services;

namespace Shonkor.Bench;

/// <summary>
/// How an answer is held against the key. A one-way door: switching after the first scored run would
/// re-grade recorded answers, so the mode is named in the report and pinned by the results file.
/// </summary>
internal enum Ap6MatchMode
{
    /// <summary>Correct := every key file and every key symbol is in the answer; extra items are counted as <c>overSelect</c>, not penalised.</summary>
    Recall,
    /// <summary>Correct := the answer's files and symbols are exactly the key's (set equality).</summary>
    Exact,
}

internal enum Ap6Majority { Correct, Incorrect, Incomplete }

/// <summary>What one run of one arm on one task came to.</summary>
internal sealed class Ap6RunVerdict
{
    public string TaskId { get; init; } = string.Empty;
    public string Class { get; init; } = string.Empty;
    public string Arm { get; init; } = string.Empty;
    public int Run { get; init; }

    /// <summary>False when the arm was not what it claims (<see cref="ArmViolation"/>) or the run never was a measurement (<see cref="NotRun"/>) — the run is listed, never counted.</summary>
    public bool Scored { get; init; }
    public string? ArmViolation { get; init; }

    /// <summary>True when the run ended on the API's or the loop's side (usage/rate limit, API error, no result event) — listed, not counted, and the driver re-runs it on <c>--resume</c>. Never <c>noAnswer</c>.</summary>
    public bool NotRun { get; init; }
    public string? NotRunReason { get; init; }

    public bool Correct { get; init; }
    public bool NoAnswer { get; init; }
    /// <summary>Answer files and symbols that are not in the key.</summary>
    public int OverSelect { get; init; }
    public int MissingFiles { get; init; }
    public int MissingSymbols { get; init; }
    /// <summary>Class C only: answer symbols whose type name matched several entries and no answer file picked one.</summary>
    public int AmbiguousSymbols { get; init; }
    public int UnmappedFiles { get; init; }
    public int UnmappedSymbols { get; init; }

    public int ToolCalls { get; init; }
    public long TokensApprox { get; init; }
    public long UsageExact { get; init; }
    public double CostUsd { get; init; }
    public long DurationMs { get; init; }
    public int Turns { get; init; }
    public int BashNonRg { get; init; }
    public int McpOverflow { get; init; }
    public int PermissionDenials { get; init; }
    public bool IsError { get; init; }

    /// <summary>The answer as compared: normalised paths and symbols (class A/B) or tokens (class C). Never raw customer text.</summary>
    public List<string> AnswerFiles { get; init; } = [];
    public List<string> AnswerSymbols { get; init; } = [];
}

/// <summary>Per task and arm: the majority over the scored runs and the token sum the gate compares.</summary>
internal sealed record Ap6ArmOutcome(Ap6Majority Majority, int ScoredRuns, int CorrectRuns, long TokensApprox);

internal sealed record Ap6TaskOutcome(string TaskId, string Class, Ap6ArmOutcome Mcp, Ap6ArmOutcome Rg);

/// <summary>The class-C gate, computed — the report prints the threshold verbatim and this arithmetic beside it.</summary>
internal sealed record Ap6Gate(
    int CorrectMcp, int CorrectRg, int BothCorrect, long TokensMcp, long TokensRg,
    bool CountCondition, bool? TokenCondition, bool Met, string Arithmetic, string Decision);

/// <summary>
/// Scores runs against the corpus key (#473). Pure: takes an <see cref="Ap6RunRecord"/>, gives an
/// <see cref="Ap6RunVerdict"/>; majority and gate are folds over verdicts. Paths compare with
/// <see cref="FilePaths.Comparer"/> (#421), symbols ordinally after normalisation, class-C answers after
/// <see cref="Ap6Anonymiser.ToTokens"/>.
/// </summary>
internal static class Ap6Scorer
{
    public const Ap6MatchMode DefaultMatchMode = Ap6MatchMode.Recall;
    public const int RunsPerArm = 3;
    public const string McpArm = "mcp";
    public const string RgArm = "rg";
    public static readonly string[] Arms = [McpArm, RgArm];

    /// <summary>The ratified class-C threshold, printed verbatim; changing this string changes the gate.</summary>
    public const string GateText =
        "MCP arm correct on at least 3 more class-C tasks than the rg arm (majority of 3 runs) AND fewer file-content tokens read at equal correctness";
    public const int GateMargin = 3;

    public const string McpToolPrefix = "mcp__shonkor__";
    public static readonly string[] RgArmTools = ["Bash", "Read"];

    /// <summary>
    /// Tools an arm's <c>init.tools</c> may list besides its own without being a violation — empty until a
    /// smoke run shows Claude Code adding a harness-neutral tool (a structured-output helper, say). Anything
    /// that reads files or the graph never belongs here.
    /// </summary>
    public static readonly string[] ToleratedTools = [];

    private static readonly Regex TrailingSpan = new(@":\d+(-\d+)?$", RegexOptions.CultureInvariant);
    private static readonly Regex Generics = new(@"<[^<>]*>", RegexOptions.CultureInvariant);
    private static readonly Regex Arity = new(@"`\d+", RegexOptions.CultureInvariant);
    private static readonly Regex Ordinal = new(@"#\d+$", RegexOptions.CultureInvariant);

    // ---------- normalisation ----------

    /// <summary>
    /// A path as an arm wrote it → repository-relative with <c>/</c>. Backticks/quotes off, <c>\</c> → <c>/</c>,
    /// <c>@/</c> and <c>./</c> off, a handle's <c>::symbol</c> tail off, a trailing <c>:line</c> off, and an absolute path
    /// under <paramref name="cwd"/> relativised. Anything absolute outside <paramref name="cwd"/> stays as is (it will not match).
    /// </summary>
    public static string NormalizePath(string raw, string cwd)
    {
        var p = raw.Trim().Trim('`', '"', '\'', ' ');
        if (p.StartsWith("@/", StringComparison.Ordinal)) p = p[2..];
        var handleTail = p.IndexOf("::", StringComparison.Ordinal);
        if (handleTail >= 0) p = p[..handleTail];
        p = p.Replace('\\', '/');
        p = TrailingSpan.Replace(p, string.Empty);
        if (Regex.IsMatch(p, @"^[A-Za-z]:/") || (p.StartsWith('/') && !string.IsNullOrEmpty(cwd) && p.StartsWith(cwd.Replace('\\', '/').TrimEnd('/') + "/", FilePaths.Comparison)))
        {
            if (FilePaths.TryGetRelative(p, cwd, out var rel)) p = rel.Replace('\\', '/');
        }
        else if (p.StartsWith('/') && OperatingSystem.IsWindows())
        {
            // A root-relative "/src/X.cs" only occurs on Windows (MSYS-style answer); on POSIX a leading
            // slash is a real absolute path outside the working directory and must stay untouched.
            p = p.TrimStart('/');
        }
        while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        return p;
    }

    /// <summary>
    /// A symbol as an arm wrote it → <c>Type</c> / <c>Type.Member</c> / a dotted name without signature, generics,
    /// arity or ordinal. A handle (<c>@/path::Ns.Type::Member#1</c>) collapses to <c>Type.Member</c>; a plain
    /// <c>Ns.Type.Member</c> keeps its dots — <see cref="SymbolMatches"/> aligns it to the key from the right, so
    /// the namespace never has to be guessed.
    /// </summary>
    public static string NormalizeSymbol(string raw)
    {
        var s = raw.Trim().Trim('`', '"', '\'', ' ');
        if (s.StartsWith("@/", StringComparison.Ordinal) && s.Contains("::", StringComparison.Ordinal))
        {
            var parts = s.Split("::", StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                var type = Clean(parts[1]).Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
                var member = parts.Length >= 3 ? Clean(parts[2]) : null;
                return string.IsNullOrEmpty(member) ? type : $"{type}.{member}";
            }
        }
        return Clean(s.Replace("::", "."));
    }

    private static string Clean(string s)
    {
        var paren = s.IndexOf('(');
        if (paren >= 0) s = s[..paren];
        while (Generics.IsMatch(s)) s = Generics.Replace(s, string.Empty);
        s = Arity.Replace(s, string.Empty);
        s = Ordinal.Replace(s, string.Empty);
        return s.Trim().Trim('.');
    }

    /// <summary>The answer names the key symbol when its dotted segments end with the key's: <c>Ns.Type.Member</c> ⊇ <c>Type.Member</c>, <c>Ns.Type</c> ⊇ <c>Type</c>; <c>Type.Member</c> does not name <c>Type</c>.</summary>
    public static bool SymbolMatches(string answer, string key)
    {
        var a = answer.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var k = key.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (k.Length == 0 || a.Length < k.Length) return false;
        for (var i = 1; i <= k.Length; i++)
            if (!string.Equals(a[^i], k[^i], StringComparison.Ordinal)) return false;
        return true;
    }

    // ---------- the arm ----------

    /// <summary>Why the run is not the arm it was started as, or <c>null</c>. Read from <c>system/init</c>, never from the flags we passed.</summary>
    public static string? ArmViolation(Ap6RunRecord r, string arm)
    {
        if (!r.SawInit) return "no system/init event in the stream";
        var tolerated = new HashSet<string>(ToleratedTools, StringComparer.Ordinal);
        var connected = r.McpServers.Where(kv => kv.Value == "connected").Select(kv => kv.Key).ToList();
        switch (arm)
        {
            case McpArm:
            {
                var foreign = r.InitTools.Where(t => !t.StartsWith(McpToolPrefix, StringComparison.Ordinal) && !tolerated.Contains(t)).ToList();
                if (foreign.Count > 0) return $"init.tools has non-shonkor tool(s): {string.Join(", ", foreign)}";
                if (!r.InitTools.Any(t => t.StartsWith(McpToolPrefix, StringComparison.Ordinal))) return "init.tools lists no mcp__shonkor__ tool";
                if (!r.McpServers.TryGetValue("shonkor", out var status)) return "init.mcp_servers has no 'shonkor' entry";
                if (status != "connected") return $"shonkor MCP server status '{status}'";
                var others = connected.Where(n => n != "shonkor").ToList();
                return others.Count > 0 ? $"other MCP server(s) connected: {string.Join(", ", others)}" : null;
            }
            case RgArm:
            {
                if (connected.Count > 0) return $"MCP server(s) connected in the rg arm: {string.Join(", ", connected)}";
                var allowed = new HashSet<string>(RgArmTools.Concat(tolerated), StringComparer.Ordinal);
                var foreign = r.InitTools.Where(t => !allowed.Contains(t)).ToList();
                if (foreign.Count > 0) return $"init.tools has tool(s) outside Bash/Read: {string.Join(", ", foreign)}";
                var missing = RgArmTools.Where(t => !r.InitTools.Contains(t, StringComparer.Ordinal)).ToList();
                return missing.Count > 0 ? $"init.tools lacks {string.Join(", ", missing)}" : null;
            }
            default:
                return $"unknown arm '{arm}'";
        }
    }

    // ---------- not run ----------

    /// <summary>
    /// The <c>error_*</c> result subtypes that are outcomes of the pinned limits (#505): the arm ran out of turns,
    /// budget or structured-output retries. They are scored (<c>noAnswer</c>); every other error ended the run on
    /// the API's or the loop's side and is <see cref="NotRunReason"/>.
    /// </summary>
    public static readonly string[] MeasuredErrorSubtypes = ["error_max_turns", "error_max_budget_usd", "error_max_structured_output_retries"];

    /// <summary>
    /// The texts a subscription's usage windows and the API's throttles come back with ("You've hit your session limit ·
    /// resets 4pm", "You've hit your weekly limit", "Request rejected (429)", "rate limit") — matched case-insensitively
    /// on <see cref="Ap6RunRecord.ErrorText"/>; HTTP 429 in <c>api_error_status</c> counts without any text.
    /// </summary>
    public static readonly Regex LimitText =
        new(@"session limit|weekly limit|opus limit|sonnet limit|usage limit|rate.?limit|\(429\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public const string LimitReasonPrefix = "usage/rate limit";

    /// <summary>
    /// Why the run is not a measurement at all, or <c>null</c>. A stream without a <c>result</c> event (the process
    /// died), an <c>is_error</c> result whose subtype is not one of <see cref="MeasuredErrorSubtypes"/> — on subtype
    /// <c>success</c> that is a failed final API request with <c>api_error_status</c>, on <c>error_during_execution</c>
    /// a loop-level failure. A limit hit starts with <see cref="LimitReasonPrefix"/> so the driver can stop the set.
    /// </summary>
    public static string? NotRunReason(Ap6RunRecord r)
    {
        if (!r.SawResult) return "stream ended without a result event";
        if (!r.IsError || MeasuredErrorSubtypes.Contains(r.ResultSubtype, StringComparer.Ordinal)) return null;
        var text = string.IsNullOrWhiteSpace(r.ErrorText) ? "(no error text)" : r.ErrorText.Trim();
        var status = r.ApiErrorStatus is { } s ? $"HTTP {s.ToString(CultureInfo.InvariantCulture)}" : $"subtype {r.ResultSubtype ?? "?"}";
        var limit = r.ApiErrorStatus == 429 || LimitText.IsMatch(text);
        return $"{(limit ? LimitReasonPrefix : "API/loop error")} ({status}): {text}";
    }

    public static bool IsLimitReason(string? reason) => reason is not null && reason.StartsWith(LimitReasonPrefix, StringComparison.Ordinal);

    // ---------- one run ----------

    /// <param name="cwd">The arm's working directory (repository root) — absolute answer paths are relativised against it.</param>
    /// <param name="mapping">Class C only: the token mapping the answer is translated back through.</param>
    public static Ap6RunVerdict Score(Ap6Task task, string arm, int run, Ap6RunRecord r, Ap6MatchMode mode, string cwd, Ap6Mapping? mapping)
    {
        // A run that never was a measurement cannot violate its arm either — the not-run reason is the one listed.
        var notRun = NotRunReason(r);
        var violation = notRun is null ? ArmViolation(r, arm) : null;
        var files = (r.Answer?.Files ?? []).Select(f => NormalizePath(f, cwd)).Where(f => f.Length > 0).Distinct(FilePaths.Comparer).ToList();
        var symbols = (r.Answer?.Symbols ?? []).Select(NormalizeSymbol).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var noAnswer = notRun is null && (r.Answer is null || r.Answer.SchemaVersion != 1);

        int unmappedFiles = 0, unmappedSymbols = 0, ambiguous = 0;
        var isC = task.Class == "C";
        if (isC && mapping is not null && !noAnswer)
        {
            var t = Ap6Anonymiser.ToTokens(files, symbols, mapping);
            files = t.Files; symbols = t.Symbols;
            unmappedFiles = t.UnmappedFiles; unmappedSymbols = t.UnmappedSymbols; ambiguous = t.AmbiguousSymbols;
        }
        else if (isC)
        {
            // Without the mapping a class-C answer cannot be read back — nothing is carried, and it cannot be correct.
            files = []; symbols = [];
        }

        var keyFiles = task.Key?.Files ?? [];
        var keySymbols = task.Key?.Symbols ?? [];
        var fileComparer = isC ? StringComparer.Ordinal : FilePaths.Comparer;

        var missingFiles = keyFiles.Count(k => !files.Contains(k, fileComparer));
        var missingSymbols = keySymbols.Count(k => !symbols.Any(a => SymbolMatches(a, k)));
        var extraFiles = files.Count(a => !keyFiles.Contains(a, fileComparer));
        var extraSymbols = symbols.Count(a => !keySymbols.Any(k => SymbolMatches(a, k)));
        var overSelect = extraFiles + extraSymbols;
        var recall = notRun is null && !noAnswer && missingFiles == 0 && missingSymbols == 0;
        var correct = mode == Ap6MatchMode.Recall ? recall : recall && overSelect == 0;
        var scored = violation is null && notRun is null;

        return new Ap6RunVerdict
        {
            TaskId = task.Id ?? string.Empty, Class = task.Class ?? string.Empty, Arm = arm, Run = run,
            Scored = scored, ArmViolation = violation, NotRun = notRun is not null, NotRunReason = notRun,
            Correct = scored && correct, NoAnswer = noAnswer, OverSelect = overSelect,
            MissingFiles = missingFiles, MissingSymbols = missingSymbols,
            AmbiguousSymbols = ambiguous, UnmappedFiles = unmappedFiles, UnmappedSymbols = unmappedSymbols,
            ToolCalls = r.ToolCalls.Count, TokensApprox = r.TokensApprox, UsageExact = r.UsageExact,
            CostUsd = r.CostUsd, DurationMs = r.DurationMs, Turns = r.Turns,
            BashNonRg = r.BashNonRg, McpOverflow = r.McpOverflow, PermissionDenials = r.PermissionDenials, IsError = r.IsError,
            AnswerFiles = files, AnswerSymbols = symbols,
        };
    }

    // ---------- majority and gate ----------

    /// <summary>Correct in at least 2 of the 3 scored runs; fewer than 3 scored runs is <see cref="Ap6Majority.Incomplete"/> and never counts as correct.</summary>
    public static Ap6ArmOutcome ArmOutcome(IEnumerable<Ap6RunVerdict> verdicts)
    {
        var scored = verdicts.Where(v => v.Scored).OrderBy(v => v.Run).Take(RunsPerArm).ToList();
        var correct = scored.Count(v => v.Correct);
        var tokens = scored.Sum(v => v.TokensApprox);
        if (scored.Count < RunsPerArm) return new Ap6ArmOutcome(Ap6Majority.Incomplete, scored.Count, correct, tokens);
        return new Ap6ArmOutcome(correct * 2 > RunsPerArm ? Ap6Majority.Correct : Ap6Majority.Incorrect, scored.Count, correct, tokens);
    }

    public static List<Ap6TaskOutcome> Outcomes(IReadOnlyList<Ap6Task> tasks, IReadOnlyList<Ap6RunVerdict> verdicts) =>
        tasks.Select(t => new Ap6TaskOutcome(
            t.Id ?? string.Empty, t.Class ?? string.Empty,
            ArmOutcome(verdicts.Where(v => v.TaskId == t.Id && v.Arm == McpArm)),
            ArmOutcome(verdicts.Where(v => v.TaskId == t.Id && v.Arm == RgArm)))).ToList();

    /// <summary>
    /// The class-C gate over its task outcomes. Count condition: <c>C_mcp − C_rg ≥ 3</c>. Token condition: over the
    /// tasks both arms got majority-correct, Σ tokensApprox(mcp) &lt; Σ tokensApprox(rg); with no such task it is
    /// not evaluable, and a condition that cannot be evaluated is not met.
    /// </summary>
    public static Ap6Gate Gate(IReadOnlyList<Ap6TaskOutcome> classC)
    {
        var cMcp = classC.Count(o => o.Mcp.Majority == Ap6Majority.Correct);
        var cRg = classC.Count(o => o.Rg.Majority == Ap6Majority.Correct);
        var both = classC.Where(o => o.Mcp.Majority == Ap6Majority.Correct && o.Rg.Majority == Ap6Majority.Correct).ToList();
        var tMcp = both.Sum(o => o.Mcp.TokensApprox);
        var tRg = both.Sum(o => o.Rg.TokensApprox);
        var countOk = cMcp - cRg >= GateMargin;
        bool? tokenOk = both.Count == 0 ? null : tMcp < tRg;
        var met = countOk && tokenOk == true;
        var arithmetic =
            $"C_mcp − C_rg = {cMcp} − {cRg} = {cMcp - cRg} ≥ {GateMargin}: {(countOk ? "yes" : "no")}; "
            + (both.Count == 0
                ? "Σ tokensApprox at equal correctness: not evaluable (0 tasks majority-correct in both arms)"
                : $"Σ tokensApprox(mcp) = {tMcp.ToString("N0", CultureInfo.InvariantCulture)} < Σ tokensApprox(rg) = {tRg.ToString("N0", CultureInfo.InvariantCulture)} over {both.Count} task(s) majority-correct in both arms: {(tokenOk == true ? "yes" : "no")}");
        return new Ap6Gate(cMcp, cRg, both.Count, tMcp, tRg, countOk, tokenOk, met, arithmetic,
            met ? "met — phases 2-4 continue" : "not met — stop");
    }
}
