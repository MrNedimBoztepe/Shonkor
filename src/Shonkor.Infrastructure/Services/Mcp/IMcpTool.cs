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
    /// Executes the tool and returns a complete JSON-RPC response string (success or error), built via
    /// <see cref="McpToolHelpers"/>.
    /// </summary>
    Task<string> ExecuteAsync(JsonElement id, JsonObject? args, McpToolContext ctx);
}
