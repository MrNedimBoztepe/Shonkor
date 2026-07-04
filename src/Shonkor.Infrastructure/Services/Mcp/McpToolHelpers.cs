// Licensed to Shonkor under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;

namespace Shonkor.Infrastructure.Services.Mcp;

/// <summary>
/// Stateless helpers shared across the MCP tools: argument coercion, node-id "handles", signature
/// rendering, symbol resolution, and JSON-RPC response builders. Extracted from the former monolithic
/// McpRequestHandler so each tool can reuse them without depending on the handler.
/// </summary>
public static class McpToolHelpers
{
    /// <summary>How many candidate hits the symbol-oriented tools pull before applying their selection heuristic.</summary>
    public const int SymbolSearchLimit = 8;

    /// <summary>Node types that count as a "declaration" when resolving a symbol to its definition.</summary>
    public static readonly string[] DeclarationTypes =
        { "Class", "Interface", "Record", "Struct", "Enum", "Method", "Property", "Constructor" };

    /// <summary>
    /// Containment/grouping edges that are structure, not semantic impact or dependency: a type's parent
    /// file, a method's parent type, or a node's Helix module. Excluded from references/find_usages so a
    /// method's real impact (its CALLS / REFERENCES_TYPE) isn't drowned by its enclosing type.
    /// </summary>
    public static readonly HashSet<string> StructuralRelationships = new(StringComparer.Ordinal)
    {
        "CONTAINS", "BELONGS_TO_MODULE"
    };

    /// <summary>A reusable JSON-null element, used to echo back an explicit <c>"id": null</c> JSON-RPC id.</summary>
    public static readonly JsonElement NullJsonElement = JsonSerializer.SerializeToElement<object?>(null);

    public static string UtcNow() => DateTime.UtcNow.ToString("o");

    /// <summary>The single source of truth for "this node IS the symbol" (case-insensitive name equality).</summary>
    public static bool IsExactNameMatch(GraphNode node, string symbol) =>
        string.Equals(node.Name, symbol, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the target symbol for symbol-oriented tools, accepting <c>query</c> as a lenient alias of <c>symbol</c>.</summary>
    public static string? ReadSymbol(JsonNode? args) =>
        args?["symbol"]?.ToString() ?? args?["query"]?.ToString();

    /// <summary>
    /// Reads an integer tool argument tolerantly: accepts a JSON number (int or float) or a numeric
    /// string, returning <paramref name="fallback"/> for null/missing/non-numeric input instead of
    /// throwing.
    /// </summary>
    public static int ReadInt(JsonNode? value, int fallback)
    {
        if (value is null) return fallback;
        try
        {
            return value.GetValueKind() switch
            {
                JsonValueKind.Number => (int)value.GetValue<double>(),
                JsonValueKind.String => int.TryParse(value.GetValue<string>(), out var s) ? s : fallback,
                _ => fallback
            };
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>
    /// Heuristic for <c>related_tests</c>: whether a file path looks like a test file across common
    /// ecosystems (xUnit/NUnit <c>*.Tests</c>, Go <c>_test</c>, Python <c>test_</c>, JS <c>.spec.</c>/
    /// <c>.test.</c>/<c>__tests__</c>).
    /// </summary>
    public static bool LooksLikeTest(string? filePath)
    {
        var p = (filePath ?? string.Empty).ToLowerInvariant();
        return p.Contains("test") || p.Contains(".spec.") || p.Contains("__tests__") || p.Contains("/spec/") || p.Contains("\\spec\\");
    }

    /// <summary>
    /// Returns the first non-empty line of <paramref name="content"/> that mentions <paramref name="name"/>
    /// (trimmed, capped at 160 chars), or <c>null</c> — a grep-like usage snippet for find_usages.
    /// </summary>
    public static string? FirstLineMentioning(string? content, string name)
    {
        if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(name)) return null;
        foreach (var rawLine in content.Split('\n'))
        {
            if (rawLine.Contains(name, StringComparison.Ordinal))
            {
                var trimmed = rawLine.Trim();
                return trimmed.Length > 160 ? trimmed[..160] + "…" : trimmed;
            }
        }
        return null;
    }

    /// <summary>
    /// Builds a compact signature line for a node from its stored properties (no body): a method/
    /// constructor as <c>modifiers returnType Name(params)</c>, a property as <c>modifiers type Name</c>,
    /// and a type as <c>modifiers kind Name</c>.
    /// </summary>
    public static string BuildSignature(GraphNode node)
    {
        var p = node.Properties;
        var mods = p.GetValueOrDefault("modifiers", string.Empty).Trim();
        string Compose(params string?[] parts) =>
            string.Join(" ", parts.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));

        switch (node.Type)
        {
            case "Method":
            case "Constructor":
                return Compose(mods, p.GetValueOrDefault("returnType", ""), $"{node.Name}({p.GetValueOrDefault("parameters", "").Trim()})");
            case "Property":
                return Compose(mods, p.GetValueOrDefault("returnType", ""), node.Name);
            default:
                return Compose(mods, node.Type.ToLowerInvariant(), node.Name);
        }
    }

    /// <summary>Returns <paramref name="path"/> relative to <paramref name="basePath"/> when contained, else the original.</summary>
    public static string Shorten(string? path, string basePath)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        if (!string.IsNullOrEmpty(basePath) && path.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
        {
            var rel = path[basePath.Length..].TrimStart('\\', '/');
            return rel.Length > 0 ? rel : path;
        }
        return path;
    }

    /// <summary>
    /// Shortens a node id to a reusable, token-cheap "handle". File-path ids under the project root are
    /// emitted as <c>@/&lt;relative&gt;</c>; other ids are left unchanged. The <c>@/</c> marker makes the
    /// transform reversible via <see cref="FromHandle"/> and unambiguous.
    /// </summary>
    public static string ToHandle(string? id, string basePath)
    {
        if (string.IsNullOrEmpty(id)) return string.Empty;
        if (!string.IsNullOrEmpty(basePath) && id.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
        {
            var rel = id[basePath.Length..].TrimStart('\\', '/');
            if (rel.Length > 0) return "@/" + rel;
        }
        return id;
    }

    /// <summary>Expands a <c>@/&lt;relative&gt;</c> handle back to a real node id; other values pass through unchanged.</summary>
    public static string FromHandle(string? handle, string basePath)
    {
        if (string.IsNullOrEmpty(handle)) return string.Empty;
        if (handle.StartsWith("@/", StringComparison.Ordinal) && !string.IsNullOrEmpty(basePath))
        {
            return basePath.TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar + handle[2..];
        }
        return handle;
    }

    /// <summary>
    /// Truncates markdown to at most <paramref name="maxChars"/> characters, preferring to cut at the
    /// last Markdown heading (## ) boundary before the limit so sections stay intact. Appends a notice.
    /// </summary>
    public static string TruncateAtBoundary(string markdown, int maxChars)
    {
        if (markdown.Length <= maxChars) return markdown;

        var slice = markdown[..maxChars];
        var boundary = slice.LastIndexOf("\n## ", StringComparison.Ordinal);
        if (boundary <= 0)
        {
            boundary = slice.LastIndexOf('\n');
        }
        if (boundary > 0)
        {
            slice = slice[..boundary];
        }

        return slice.TrimEnd() + "\n\n> [!NOTE]\n> Capsule truncated to fit the requested character budget. Increase maxChars or narrow the query for more detail.";
    }

    /// <summary>
    /// Resolves a free-text symbol to its best-matching definition node: prefers an exact-name declaration
    /// (Class/Method/…), then any exact-name node, then the first declaration, then the first hit.
    /// </summary>
    public static async Task<GraphNode?> ResolveDefinitionAsync(IGraphStorageProvider storage, string symbol)
    {
        var hits = (await storage.SearchAsync(symbol, SymbolSearchLimit).ConfigureAwait(false)).Select(h => h.Node).ToList();
        return hits.FirstOrDefault(n => IsExactNameMatch(n, symbol) && DeclarationTypes.Contains(n.Type))
            ?? hits.FirstOrDefault(n => IsExactNameMatch(n, symbol))
            ?? hits.FirstOrDefault(n => DeclarationTypes.Contains(n.Type))
            ?? hits.FirstOrDefault();
    }

    /// <summary>
    /// A compact, lower-cased tag for an edge's provenance tier (<c>[extracted]</c> / <c>[inferred]</c> /
    /// <c>[ambiguous]</c>), appended to edge lines so an agent sees whether a relationship is a proven fact
    /// or a heuristic guess — Shonkor's core trust signal.
    /// </summary>
    public static string ProvenanceTag(Provenance provenance) => provenance switch
    {
        Provenance.Extracted => "[extracted]",
        Provenance.Inferred => "[inferred]",
        Provenance.Ambiguous => "[ambiguous]",
        _ => "[?]"
    };

    /// <summary>
    /// Reads the optional <c>provenance</c> filter argument and returns the maximum uncertainty tier to
    /// admit (edges with a tier at or below it pass): <c>extracted</c> → only proven edges; <c>inferred</c>
    /// → proven + heuristic, excluding ambiguous; <c>all</c>/<c>ambiguous</c>/missing → no filter (null).
    /// Lets a caller demand hard-extracted-only impact analysis.
    /// </summary>
    public static Provenance? ReadProvenanceFilter(JsonNode? args)
    {
        var raw = args?["provenance"]?.ToString();
        return raw?.Trim().ToLowerInvariant() switch
        {
            "extracted" => Provenance.Extracted,
            "inferred" => Provenance.Inferred,
            _ => null // "all", "ambiguous", null, or anything unrecognized → no filtering
        };
    }

    /// <summary>Whether <paramref name="provenance"/> is admitted by an optional max-uncertainty filter (null = admit all).</summary>
    public static bool PassesProvenance(Provenance provenance, Provenance? maxUncertainty) =>
        maxUncertainty is null || (int)provenance <= (int)maxUncertainty;

    // ---- JSON-RPC response builders (stateless) ----------------------------------------------------

    public static string SendResponse(JsonElement id, object result) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result });

    public static string SendToolResponse(JsonElement id, string text) =>
        SendResponse(id, new { content = new[] { new { type = "text", text } } });

    public static string SendError(JsonElement id, int code, string message) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code, message } });
}
