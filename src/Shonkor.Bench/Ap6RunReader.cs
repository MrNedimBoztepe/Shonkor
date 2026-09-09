// Licensed to Shonkor under the MIT License.

using System.Text.Json;
using System.Text.RegularExpressions;

namespace Shonkor.Bench;

/// <summary>One tool invocation of the main conversation, as the arm made it.</summary>
/// <param name="Name">The tool name (<c>mcp__shonkor__locate</c>, <c>Bash</c>, <c>Read</c> …).</param>
/// <param name="Input">The raw JSON of the arguments — anonymised before it reaches a results file for class C.</param>
internal sealed record Ap6ToolCall(string Name, string Input);

/// <summary>The structured answer an arm emitted, in the shape <c>answer-schema.json</c> fixes.</summary>
internal sealed record Ap6Answer(int? SchemaVersion, List<string> Files, List<string> Symbols);

/// <summary>
/// What one <c>claude -p --output-format stream-json</c> run contained, reduced to the facts the scorer
/// and the report use. Read by <see cref="Ap6RunReader"/>; serialised as <c>meta.json</c> beside the stream.
/// </summary>
internal sealed class Ap6RunRecord
{
    /// <summary><c>system/init.tools</c> — what the arm could call. The arm check reads this, not the flags we passed.</summary>
    public List<string> InitTools { get; set; } = [];

    /// <summary><c>system/init.mcp_servers</c> as name → status.</summary>
    public Dictionary<string, string> McpServers { get; set; } = new(StringComparer.Ordinal);

    public string? Model { get; set; }
    public bool SawInit { get; set; }
    public bool SawResult { get; set; }
    public string? ResultSubtype { get; set; }
    public bool IsError { get; set; }
    public double CostUsd { get; set; }
    public long DurationMs { get; set; }
    public int Turns { get; set; }
    public int PermissionDenials { get; set; }

    /// <summary>Tool calls of the main conversation only — subagent events (<c>parent_tool_use_id != null</c>) are not the arm.</summary>
    public List<Ap6ToolCall> ToolCalls { get; set; } = [];

    /// <summary>Σ characters of every <c>tool_result</c> text block in the main conversation.</summary>
    public long ToolResultChars { get; set; }

    /// <summary><see cref="ToolResultChars"/> / 4, rounded — the same approximation for both arms; the gate's token column.</summary>
    public long TokensApprox => (long)Math.Round(ToolResultChars / 4.0, MidpointRounding.AwayFromZero);

    /// <summary>Σ <c>message.usage.{input,cache_creation,cache_read,output}</c> over distinct assistant messages — the billed side, for the second column.</summary>
    public long UsageExact { get; set; }

    /// <summary>Bash calls whose command does not start with <c>rg</c> — built-in read-only commands the rg arm may use; counted, not forbidden.</summary>
    public int BashNonRg { get; set; }

    /// <summary>MCP tool results Claude Code replaced with its "exceeds maximum allowed tokens" stand-in — the arm never saw the payload.</summary>
    public int McpOverflow { get; set; }

    /// <summary>Lines of the stream that were not JSON objects.</summary>
    public int MalformedLines { get; set; }

    /// <summary>The raw <c>structured_output</c> of the last <c>result</c> event, when any.</summary>
    public string? StructuredOutputJson { get; set; }

    /// <summary>The answer, or <c>null</c> when there is no parseable one (→ <c>noAnswer</c>).</summary>
    public Ap6Answer? Answer { get; set; }
}

/// <summary>
/// Parses a <c>stream.jsonl</c> into an <see cref="Ap6RunRecord"/>. Pure over the text; tolerant of
/// anything it does not know (the stream format grows with every Claude Code release), strict about what
/// it counts.
/// </summary>
internal static class Ap6RunReader
{
    /// <summary>The stand-in Claude Code writes instead of an oversized MCP tool result; matched case-insensitively on the result text.</summary>
    public static readonly Regex OverflowMarker =
        new(@"exceeds maximum allowed tokens", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static Ap6RunRecord Read(string streamText)
    {
        var r = new Ap6RunRecord();
        var usageByMessage = new Dictionary<string, long>(StringComparer.Ordinal);
        var anonymousUsage = 0L;

        foreach (var rawLine in streamText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { r.MalformedLines++; continue; }
            using (doc)
            {
                var e = doc.RootElement;
                if (e.ValueKind != JsonValueKind.Object) { r.MalformedLines++; continue; }
                var type = Str(e, "type");
                switch (type)
                {
                    case "system" when Str(e, "subtype") == "init":
                        ReadInit(e, r);
                        break;
                    case "assistant" when IsMainConversation(e):
                        ReadAssistant(e, r, usageByMessage, ref anonymousUsage);
                        break;
                    case "user" when IsMainConversation(e):
                        ReadUser(e, r);
                        break;
                    case "result":
                        ReadResult(e, r);
                        break;
                }
            }
        }
        r.UsageExact = usageByMessage.Values.Sum() + anonymousUsage;
        return r;
    }

    /// <summary>Subagent traffic carries a <c>parent_tool_use_id</c>; neither arm has subagents, but a stream that did must not be credited for their reads.</summary>
    private static bool IsMainConversation(JsonElement e) =>
        !e.TryGetProperty("parent_tool_use_id", out var p) || p.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;

    private static void ReadInit(JsonElement e, Ap6RunRecord r)
    {
        r.SawInit = true;
        r.Model = Str(e, "model");
        if (e.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
            r.InitTools = tools.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!).ToList();
        if (e.TryGetProperty("mcp_servers", out var servers) && servers.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in servers.EnumerateArray())
            {
                var name = Str(s, "name");
                if (name is not null) r.McpServers[name] = Str(s, "status") ?? "?";
            }
        }
    }

    private static void ReadAssistant(JsonElement e, Ap6RunRecord r, Dictionary<string, long> usageByMessage, ref long anonymousUsage)
    {
        if (!e.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) return;
        if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (Str(block, "type") != "tool_use") continue;
                var name = Str(block, "name") ?? "?";
                var input = block.TryGetProperty("input", out var inp) ? inp.GetRawText() : "{}";
                r.ToolCalls.Add(new Ap6ToolCall(name, input));
                if (name == "Bash" && !IsRgCommand(inp)) r.BashNonRg++;
            }
        }
        if (msg.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            var sum = Num(usage, "input_tokens") + Num(usage, "cache_creation_input_tokens") + Num(usage, "cache_read_input_tokens") + Num(usage, "output_tokens");
            // One message is streamed as several assistant events (one per content block), each carrying the
            // message's usage — keyed by message id so it is counted once.
            var id = Str(msg, "id");
            if (id is not null) usageByMessage[id] = sum; else anonymousUsage += sum;
        }
    }

    /// <summary>The first word of the command is <c>rg</c> — pipes after it (<c>rg … | head</c>) still count as rg.</summary>
    public static bool IsRgCommand(JsonElement input)
    {
        var command = input.ValueKind == JsonValueKind.Object ? Str(input, "command") : null;
        if (command is null) return false;
        var first = command.TrimStart().Split((char[])[' ', '\t', '\n'], 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return first == "rg" || first.EndsWith("/rg", StringComparison.Ordinal) || first.EndsWith("/rg.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static void ReadUser(JsonElement e, Ap6RunRecord r)
    {
        if (!e.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
        foreach (var block in content.EnumerateArray())
        {
            if (Str(block, "type") != "tool_result") continue;
            var text = ToolResultText(block);
            r.ToolResultChars += text.Length;
            if (OverflowMarker.IsMatch(text)) r.McpOverflow++;
        }
    }

    /// <summary><c>content</c> is a string or an array of blocks; only <c>text</c> blocks are text the arm read.</summary>
    private static string ToolResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var c)) return string.Empty;
        if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? string.Empty;
        if (c.ValueKind != JsonValueKind.Array) return string.Empty;
        return string.Concat(c.EnumerateArray().Where(b => Str(b, "type") == "text").Select(b => Str(b, "text") ?? string.Empty));
    }

    private static void ReadResult(JsonElement e, Ap6RunRecord r)
    {
        r.SawResult = true;
        r.ResultSubtype = Str(e, "subtype");
        r.IsError = e.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
        r.CostUsd = e.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number ? cost.GetDouble() : 0;
        r.DurationMs = Num(e, "duration_ms");
        r.Turns = (int)Num(e, "num_turns");
        r.PermissionDenials = e.TryGetProperty("permission_denials", out var pd) && pd.ValueKind == JsonValueKind.Array ? pd.GetArrayLength() : 0;
        r.StructuredOutputJson = e.TryGetProperty("structured_output", out var so) && so.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? so.GetRawText() : null;
        r.Answer = ParseAnswer(r.StructuredOutputJson);
    }

    /// <summary>The answer, if it is an object with a string array for <c>files</c> and <c>symbols</c>; anything else is no answer.</summary>
    public static Ap6Answer? ParseAnswer(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            int? version = root.TryGetProperty("schemaVersion", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var vi) ? vi : null;
            var files = Strings(root, "files");
            var symbols = Strings(root, "symbols");
            return files is null || symbols is null ? null : new Ap6Answer(version, files, symbols);
        }
        catch (JsonException) { return null; }
    }

    private static List<string>? Strings(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Array) return null;
        return a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList();
    }

    private static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static long Num(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var n) ? n : 0;
}
