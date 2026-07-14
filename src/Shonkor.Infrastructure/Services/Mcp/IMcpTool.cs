// Licensed to Shonkor under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shonkor.Infrastructure.Services.Mcp;

/// <summary>
/// One MCP tool: its name, its JSON schema (for <c>tools/list</c>), an availability gate, and its
/// execution. Each tool is a small, independently testable class instead of a case in a giant switch.
/// </summary>
public interface IMcpTool
{
    /// <summary>The tool name used in <c>tools/list</c> and <c>tools/call</c>.</summary>
    string Name { get; }

    /// <summary>The schema object advertised in <c>tools/list</c> (name + description + inputSchema).</summary>
    object GetSchema();

    /// <summary>
    /// Whether this tool should be advertised/usable for the given context. Defaults to always available;
    /// capability-gated tools (e.g. semantic search) override this.
    /// </summary>
    bool IsAvailable(McpToolContext ctx) => true;

    /// <summary>
    /// The argument names this tool treats as <b>filesystem paths</b> (#105). The dispatcher resolves and
    /// <b>contains</b> each of them before <see cref="ExecuteAsync"/> is ever entered, and rejects the call
    /// outright if one escapes the project root — so a tool receives paths that are already vetted and
    /// already absolute.
    ///
    /// <para>
    /// Containment used to be applied <i>per tool</i>, which meant six copies of the same dance and a
    /// standing invitation for the seventh tool to forget it. Now the guard is structural: a tool that
    /// declares its path arguments cannot skip it, and one that <i>forgets to declare them</i> fails
    /// <c>PathArgumentsAreDeclared</c> — the schema is cross-checked against this list, so "forgot" is a
    /// build error rather than a vulnerability.
    /// </para>
    ///
    /// <para>An array-valued argument (e.g. <c>paths</c>) is contained element by element.</para>
    /// </summary>
    IReadOnlyList<string> PathArguments => [];

    /// <summary>
    /// Executes the tool and returns a complete JSON-RPC response string (success or error), built via
    /// <see cref="McpToolHelpers"/>.
    /// </summary>
    Task<string> ExecuteAsync(JsonElement id, JsonObject? args, McpToolContext ctx);
}
