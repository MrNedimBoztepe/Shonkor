// Licensed to Shonkor under the MIT License.

extern alias bench;

using System.Text.Json;
using bench::Shonkor.Bench;

namespace Shonkor.Tests;

/// <summary>
/// Synthetic <c>stream.jsonl</c> events and corpus tasks for the AP6 harness tests (#473). The shapes follow
/// <c>claude -p --output-format stream-json --verbose</c>: a <c>system/init</c> event, <c>assistant</c> events
/// with content blocks, <c>user</c> events carrying <c>tool_result</c> blocks, and one final <c>result</c>.
/// No run is needed — the reader and scorer are pure over this text.
/// </summary>
internal static class Ap6Fixtures
{
    public static readonly string[] McpTools = ["mcp__shonkor__locate", "mcp__shonkor__get_source", "mcp__shonkor__find_usages"];
    public static readonly string[] RgTools = ["Bash", "Read"];

    public static string Init(IEnumerable<string> tools, params (string Name, string Status)[] servers) =>
        JsonSerializer.Serialize(new
        {
            type = "system", subtype = "init", session_id = "s1", model = "claude-test",
            tools = tools.ToArray(),
            mcp_servers = servers.Select(s => new { name = s.Name, status = s.Status }).ToArray(),
        });

    public static string ToolUse(string name, object input, string messageId = "m1", string? parentToolUseId = null, int inputTokens = 100, int outputTokens = 20) =>
        JsonSerializer.Serialize(new
        {
            type = "assistant",
            parent_tool_use_id = parentToolUseId,
            message = new
            {
                id = messageId,
                content = new object[] { new { type = "tool_use", id = "t_" + messageId, name, input } },
                usage = new { input_tokens = inputTokens, cache_creation_input_tokens = 0, cache_read_input_tokens = 0, output_tokens = outputTokens },
            },
        });

    public static string Text(string text, string messageId = "m9", int inputTokens = 100, int outputTokens = 20) =>
        JsonSerializer.Serialize(new
        {
            type = "assistant", parent_tool_use_id = (string?)null,
            message = new
            {
                id = messageId,
                content = new object[] { new { type = "text", text } },
                usage = new { input_tokens = inputTokens, cache_creation_input_tokens = 0, cache_read_input_tokens = 0, output_tokens = outputTokens },
            },
        });

    /// <summary>A tool result whose <c>content</c> is a plain string.</summary>
    public static string ToolResultString(string text, string? parentToolUseId = null) =>
        JsonSerializer.Serialize(new
        {
            type = "user", parent_tool_use_id = parentToolUseId,
            message = new { content = new object[] { new { type = "tool_result", tool_use_id = "t_m1", content = text } } },
        });

    /// <summary>A tool result whose <c>content</c> is an array of blocks (the MCP shape).</summary>
    public static string ToolResultBlocks(params string[] texts) =>
        JsonSerializer.Serialize(new
        {
            type = "user", parent_tool_use_id = (string?)null,
            message = new { content = new object[] { new { type = "tool_result", tool_use_id = "t_m1", content = texts.Select(t => new { type = "text", text = t }).ToArray() } } },
        });

    public static string Result(object? structuredOutput, double cost = 0.05, int turns = 3, long durationMs = 1234, bool isError = false, int denials = 0) =>
        JsonSerializer.Serialize(new
        {
            type = "result", subtype = isError ? "error_max_turns" : "success", is_error = isError,
            total_cost_usd = cost, duration_ms = durationMs, num_turns = turns,
            structured_output = structuredOutput,
            permission_denials = Enumerable.Range(0, denials).Select(_ => new { tool_name = "Bash" }).ToArray(),
        });

    public static object Answer(string[] files, string[] symbols, int version = 1) => new { schemaVersion = version, files, symbols };

    public static string Stream(params string[] events) => string.Join("\n", events) + "\n";

    /// <summary>A complete, well-formed MCP-arm stream that answers with <paramref name="files"/> / <paramref name="symbols"/>.</summary>
    public static string McpStream(string[] files, string[] symbols, string resultText = "hit", int version = 1) =>
        Stream(
            Init(McpTools, ("shonkor", "connected")),
            ToolUse("mcp__shonkor__locate", new { query = "Foo" }),
            ToolResultBlocks(resultText),
            Result(Answer(files, symbols, version)));

    /// <summary>A complete, well-formed rg-arm stream.</summary>
    public static string RgStream(string[] files, string[] symbols, string command = "rg -n Foo src", string resultText = "src/X/Foo.cs:1:class Foo") =>
        Stream(
            Init(RgTools),
            ToolUse("Bash", new { command }),
            ToolResultString(resultText),
            Result(Answer(files, symbols)));

    public static Ap6Task Task(string id, string cls, string? query = null, string[]? files = null, string[]? symbols = null, string? @ref = null)
    {
        var (corpus, expectation, method) = cls switch
        {
            "A" => ("Brain", "rg", "merge-commit"),
            "B" => ("Brain", "graph", "merge-commit"),
            _ => ("Corpus-A", "graph-only", "unicorn-yml"),
        };
        return new Ap6Task(1, id, cls, corpus, query ?? $"Which file declares the thing of {id}?",
            new Ap6Key((files ?? ["src/X/Foo.cs"]).ToList(), (symbols ?? ["Foo"]).ToList()),
            new Ap6KeySource(method, @ref ?? (cls == "C" ? "9d7f9ce" : "de44654380032c1766d089d859c7e3c86ac79a74"), "rule"),
            false, expectation);
    }

    /// <summary>A small class-C mapping: two controllers sharing a simple name (#488), a view, a rendering, a model.</summary>
    public static Ap6Mapping Mapping(string corpusRevision = "9d7f9ce") =>
        new(1, "C:/Corpora/Acme", corpusRevision, ["acme", "acmesite"],
            new Dictionary<string, Ap6MappingEntry>(StringComparer.Ordinal)
            {
                ["Rendering-01"] = new("rendering", "serialization/Renderings/Hero Banner.yml", "Hero Banner", null),
                ["Controller-01"] = new("controller", "src/Feature/Hero/HeroController.cs", "HeroController", "Acme.Feature.Hero.HeroController"),
                ["Controller-02"] = new("controller", "src/Feature/Legacy/HeroController.cs", "HeroController", "Acme.Legacy.HeroController"),
                ["Controller-03"] = new("controller", "src/Feature/Nav/NavController.cs", "NavController", "Acme.Feature.Nav.NavController"),
                ["View-01"] = new("view", "src/Feature/Hero/Views/Hero/Index.cshtml", "Index.cshtml", null),
                ["Model-01"] = new("model", "src/Feature/Hero/Models/HeroModel.cs", "HeroModel", "Acme.Feature.Hero.Models.HeroModel"),
            });

    public static Ap6Preconditions.World HealthyWorld(string brainHead = "de44654380032c1766d089d859c7e3c86ac79a74", string corpusRevision = "9d7f9ce") =>
        new(brainHead, brainHead, [], _ => true, corpusRevision, corpusRevision, []);
}
