// Licensed to Shonkor under the MIT License.

namespace Shonkor.Infrastructure.Services.Mcp;

/// <summary>
/// Stable, machine-readable identities for the ways an MCP call can fail (#120).
/// <para>
/// TICKET-209 made relay errors generic (no filesystem paths, no SQL) and TICKET-210 moved execution
/// failures into <c>isError:true</c>. Both were right, and both left the failure surface **prose-only**: a
/// client that wants to branch on *why* a call failed had to string-match English. These codes give each
/// failure an identity that survives rewording; the human message stays alongside, unconstrained.
/// </para>
/// <para>
/// The code travels in <c>error.data.code</c> for protocol errors and in <c>result._meta.code</c> for
/// <c>isError</c> results, so a client can switch on it without parsing prose.
/// </para>
/// </summary>
public static class McpErrorCode
{
    /// <summary>A required argument was absent.</summary>
    public const string MissingParameter = "missing_parameter";

    /// <summary>A file argument resolved outside the project root (<c>TryResolveContainedPath</c> rejected it).</summary>
    public const string PathOutsideRoot = "path_outside_root";

    /// <summary>An explicitly requested <c>projectName</c> is not registered.</summary>
    public const string ProjectNotFound = "project_not_found";

    /// <summary>
    /// The named symbol does not exist in the graph. This is the agent's mistake — a typo, or a symbol it
    /// invented — and is deliberately distinct from "the symbol exists and has no results" (#118).
    /// </summary>
    public const string SymbolNotFound = "symbol_not_found";

    /// <summary>The named file is not in the graph (never indexed, or deleted since).</summary>
    public const string FileNotIndexed = "file_not_indexed";

    /// <summary>The tool needs a backend (e.g. an embedding model) that is not wired or not reachable.</summary>
    public const string BackendUnavailable = "backend_unavailable";

    /// <summary>An unexpected server-side failure. The catch-all; carries no detail by design.</summary>
    public const string ToolFailed = "tool_failed";
}

/// <summary>
/// Thrown by a tool to fail with a <b>stable identity</b> rather than prose alone (#120).
/// <para>
/// <see cref="IsArgumentError"/> chooses the channel, and the distinction is not cosmetic:
/// </para>
/// <list type="bullet">
///   <item><b>true</b> → JSON-RPC <c>-32602</c>. The caller sent something invalid; the request never became
///   a meaningful execution.</item>
///   <item><b>false</b> → a successful response carrying <c>isError:true</c>. Per the MCP spec this is how an
///   execution failure reaches the <i>model</i> — a JSON-RPC error is swallowed by the client, so the model
///   never learns the call failed and cannot adapt.</item>
/// </list>
/// </summary>
public sealed class McpToolException : Exception
{
    public string Code { get; }

    /// <summary>True → JSON-RPC <c>-32602</c>; false → an <c>isError:true</c> result the model can see.</summary>
    public bool IsArgumentError { get; }

    public McpToolException(string code, string message, bool isArgumentError = false) : base(message)
    {
        Code = code;
        IsArgumentError = isArgumentError;
    }

    /// <summary>
    /// The named symbol is not in the graph (#118). Surfaced as <c>isError</c> — NOT as a bland empty answer —
    /// because an agent that invented or mistyped a symbol must be able to tell that apart from the genuine,
    /// useful finding "this symbol exists and nothing depends on it".
    /// </summary>
    public static McpToolException SymbolNotFound(string symbol) => new(
        McpErrorCode.SymbolNotFound,
        $"No definition found for '{symbol}'. The symbol is not in the graph — check the spelling, or run " +
        "`search_graph` / `locate` to find the real name. (This is not the same as 'the symbol exists but has " +
        "no results'.)");

    /// <summary>The named file is not in the graph — never indexed, or deleted since the last index (#118).</summary>
    public static McpToolException FileNotIndexed(string path) => new(
        McpErrorCode.FileNotIndexed,
        $"File '{path}' is not in the graph. It may never have been indexed, or was deleted since. Run " +
        "`freshness` to see the project's drift, or `reindex_file` if you just created it.");
}
