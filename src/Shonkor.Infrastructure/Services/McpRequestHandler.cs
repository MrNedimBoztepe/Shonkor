// Licensed to Shonkor under the MIT License.

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services.Mcp;

namespace Shonkor.Infrastructure.Services;

/// <summary>
/// A lightweight, robust Model Context Protocol (MCP) server communicating over standard input/output
/// (stdio) using JSON-RPC. It owns only the transport (stdio loop) and the JSON-RPC envelope
/// (initialize / tools/list / tools/call); every tool is an <see cref="IMcpTool"/> resolved from the
/// <see cref="McpToolRegistry"/>, and all shared state/helpers live in <see cref="McpToolContext"/> and
/// <see cref="McpToolHelpers"/>.
/// </summary>
public sealed class McpRequestHandler
{
    /// <summary>Shared services + session state + stateful helpers handed to every tool.</summary>
    private readonly McpToolContext _ctx;

    /// <summary>The set of tools this server exposes (resolves tools/call and tools/list).</summary>
    private readonly McpToolRegistry _registry;

    /// <summary>MCP protocol revision used as a fallback when the client doesn't request one in <c>initialize</c>.</summary>
    private const string DefaultProtocolVersion = "2025-06-18";

    /// <summary>
    /// The protocol revisions this server actually speaks. Its surface (initialize / ping / tools/list /
    /// tools/call) is identical across these revisions. Per the spec, a client asking for a revision that
    /// is NOT in this set must be answered with one we do support (<see cref="DefaultProtocolVersion"/>)
    /// rather than have its own string echoed back — echoing would claim conformance we can't guarantee.
    /// </summary>
    private static readonly HashSet<string> SupportedProtocolVersions = new(StringComparer.Ordinal)
    {
        "2025-06-18", "2025-03-26", "2024-11-05"
    };

    /// <summary>
    /// Server version reported in the <c>initialize</c> handshake, read from the running assembly's
    /// informational version (set repo-wide in Directory.Build.props) — the single source of truth.
    /// The <c>+commithash</c> suffix, if any, is trimmed.
    /// </summary>
    private static readonly string ServerVersion =
        (Assembly.GetEntryAssembly() ?? typeof(McpRequestHandler).Assembly)
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion?.Split('+')[0] ?? "0.0.0";

    /// <summary>A reusable JSON-null element, used to echo back an explicit <c>"id": null</c> JSON-RPC id.</summary>
    private static readonly JsonElement NullJsonElement = JsonSerializer.SerializeToElement<object?>(null);

    public McpRequestHandler(
        ProjectManager projectManager,
        ContextCapsuleSynthesizer synthesizer,
        string? contextProjectName = null,
        bool lockToContextProject = false,
        IEmbeddingService? embeddingService = null,
        IEnumerable<IFileParser>? fileParsers = null,
        SemanticCompilationCache? compilationCache = null,
        bool persistentSession = true)
    {
        ArgumentNullException.ThrowIfNull(projectManager);
        ArgumentNullException.ThrowIfNull(synthesizer);

        _ctx = new McpToolContext(projectManager, synthesizer, contextProjectName, lockToContextProject,
            embeddingService, fileParsers, compilationCache, persistentSession);
        _registry = new McpToolRegistry(McpToolRegistryFactory.CreateTools());
    }

    /// <summary>
    /// Starts the MCP JSON-RPC standard input/output listening loop. Runs until the input stream is closed (EOF).
    /// </summary>
    public async Task StartAsync()
    {
        // Set console encoding to UTF-8 to correctly handle special characters and emojis.
        Console.InputEncoding = System.Text.Encoding.UTF8;
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        using var reader = new StreamReader(Console.OpenStandardInput(), System.Text.Encoding.UTF8);

        while (true)
        {
            try
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null)
                {
                    break; // EOF
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var response = await ProcessJsonRpcMessageAsync(line).ConfigureAwait(false);
                if (response != null)
                {
                    Console.WriteLine(response);
                }
            }
            catch (Exception ex)
            {
                // Errors go to stderr because stdout is reserved strictly for JSON-RPC messages.
                Console.Error.WriteLine($"[MCP Error] {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Processes one JSON-RPC message. Returns the response JSON, or <c>null</c> for a true notification
    /// (a message with no "id" key) — the ONLY case with no response. A parse error, an invalid request or
    /// a failing tool all produce a response, so a caller can treat <c>null</c> as "nothing to send".
    /// </summary>
    public async Task<string?> ProcessJsonRpcMessageAsync(string json)
    {
        JsonNode? idNode = null;
        try
        {
            JsonNode? document;
            try
            {
                document = JsonNode.Parse(json);
            }
            catch (JsonException)
            {
                // Spec: malformed JSON → -32700 Parse error with a null id. Previously we returned nothing,
                // leaving a client that did send an id waiting forever.
                return SendError(NullJsonElement, -32700, "Parse error: the request is not valid JSON.");
            }

            if (document is not JsonObject obj)
            {
                return SendError(NullJsonElement, -32600, "Invalid Request: the payload is not a JSON-RPC object.");
            }

            var method = obj["method"]?.ToString();
            idNode = obj["id"];

            // JSON-RPC 2.0 notifications have NO "id" key at all (key absent from the object).
            // A request with "id": null is a valid (if unusual) request and expects a response.
            // JsonNode returns null both for a missing key AND for an explicit JSON null value —
            // we disambiguate by checking whether the key is actually present in the object.
            if (!obj.ContainsKey("id"))
            {
                // True notification — no response should be sent.
                return null;
            }

            // "id": null is an unusual but valid JSON-RPC id; idNode is null for an explicit JSON null,
            // so fall back to a JSON-null element rather than dereferencing it.
            var id = idNode?.GetValue<JsonElement>() ?? NullJsonElement;

            switch (method)
            {
                case "initialize":
                    // Negotiate: honour the client's requested revision only when we actually speak it,
                    // otherwise answer with the revision we do support. Never echo an unknown version back.
                    var requestedProto = obj["params"]?["protocolVersion"]?.ToString();
                    var agreedProto = !string.IsNullOrWhiteSpace(requestedProto) && SupportedProtocolVersions.Contains(requestedProto)
                        ? requestedProto
                        : DefaultProtocolVersion;
                    return SendResponse(id, new
                    {
                        protocolVersion = agreedProto,
                        capabilities = new { tools = new { } },
                        serverInfo = new { name = "Shonkor MCP Server", version = ServerVersion }
                    });

                case "ping":
                    // Spec: a server MUST respond to ping with an empty result.
                    return SendResponse(id, new { });

                case "tools/list":
                    // All schemas come from the registry, capability-filtered (e.g. search_semantic only
                    // appears when an embedding backend is wired).
                    return SendResponse(id, new { tools = _registry.GetSchemas(_ctx) });

                case "tools/call":
                    var toolName = obj["params"]?["name"]?.ToString();
                    var args = obj["params"]?["arguments"] as JsonObject;
                    return await HandleToolCallAsync(id, toolName, args).ConfigureAwait(false);

                default:
                    return SendError(id, -32601, $"Method not found: '{method}'");
            }
        }
        catch (Exception ex)
        {
            // The full detail (including any file paths / SQL text) goes ONLY to the server log; the
            // client-facing message is generic so a relay response can't exfiltrate internal paths.
            Console.Error.WriteLine($"[MCP Internal Error] {ex.Message}\n{ex.StackTrace}");
            if (idNode != null)
            {
                try
                {
                    return SendError(idNode.GetValue<JsonElement>(), -32603, "Internal error while processing the request (see server log for details).");
                }
                catch
                {
                    // Ignore error serialization issues to prevent recursive crashes.
                }
            }
            return null;
        }
    }

    public async Task<string> HandleToolCallAsync(JsonElement id, string? toolName, JsonObject? args)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return SendError(id, -32602, "Missing parameter: 'name'");
        }

        try
        {
            // IsAvailable gates only tools/list advertising — a tool that is called anyway still runs and
            // returns its own graceful "unavailable" message rather than a generic tool-not-found error.
            var tool = _registry.Find(toolName);
            if (tool == null)
            {
                return SendError(id, -32601, $"Tool not found: '{toolName}'");
            }
            return await tool.ExecuteAsync(id, args, _ctx).ConfigureAwait(false);
        }
        catch (KeyNotFoundException ex)
        {
            // E.g. an explicitly requested projectName that isn't registered — an invalid ARGUMENT, not a
            // failed execution, so it stays a protocol error (-32602). Its message names only the project.
            return SendError(id, -32602, ex.Message);
        }
        catch (Exception ex)
        {
            // Spec (2025-06-18): an error raised while EXECUTING a tool belongs in the result as
            // isError:true, not in the JSON-RPC error channel — a protocol error is swallowed by the
            // client, so the model never learns the call failed and cannot adapt.
            //
            // The text stays free of ex.Message (TICKET-209/M11: it can carry filesystem paths or SQL);
            // the tool name and exception TYPE are safe to surface and are what the model can act on.
            // Full detail (message + stack) goes only to the server log.
            Console.Error.WriteLine($"[MCP Tool Error] {ex.Message}\n{ex.StackTrace}");
            return SendToolError(id,
                $"Tool '{toolName}' failed to execute ({ex.GetType().Name}). This is a server-side failure, not a bad argument — see the server log for details. Try a different approach or a narrower query.");
        }
    }

    private static string SendResponse(JsonElement id, object result) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result });

    private static string SendError(JsonElement id, int code, string message) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code, message } });

    /// <summary>
    /// A successful JSON-RPC response whose RESULT reports a failed tool execution (<c>isError: true</c>),
    /// per the MCP spec. Unlike a JSON-RPC error this reaches the model, which can then adapt.
    /// </summary>
    private static string SendToolError(JsonElement id, string text) =>
        SendResponse(id, new { content = new[] { new { type = "text", text } }, isError = true });
}
