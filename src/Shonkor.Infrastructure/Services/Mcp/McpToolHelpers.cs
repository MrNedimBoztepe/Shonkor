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

    /// <summary>Upper bound for a caller-supplied <c>limit</c> — a search result set is never unbounded.</summary>
    public const int MaxResultLimit = 100;

    /// <summary>Upper bound for a caller-supplied <c>hops</c>: subgraph size grows super-linearly per hop.</summary>
    public const int MaxHops = 5;

    /// <summary>Upper bound for a caller-supplied <c>maxHops</c> on path finding (a chain, not a neighbourhood).</summary>
    public const int MaxPathHops = 10;

    /// <summary>
    /// Default cap on a single tool's text output (~32 KB ≈ 8k tokens). Applied when the caller supplies no
    /// <c>maxChars</c>, so a tool can never blow an agent's context window by default; an explicit
    /// <c>maxChars</c> raises it deliberately.
    /// </summary>
    public const int DefaultOutputCapChars = 32 * 1024;

    /// <summary>Reads an optional <c>maxChars</c>, defaulting (and falling back for ≤ 0) to <see cref="DefaultOutputCapChars"/>.</summary>
    public static int ReadOutputCap(JsonNode? maxCharsArg)
    {
        var value = ReadInt(maxCharsArg, DefaultOutputCapChars);
        return value > 0 ? value : DefaultOutputCapChars;
    }

    /// <summary>Truncates <paramref name="text"/> to <paramref name="maxChars"/>, appending a hint on how to get more.</summary>
    public static string CapOutput(string text, int maxChars, string hint)
    {
        if (text.Length <= maxChars) return text;
        return text[..maxChars].TrimEnd() + $"\n… (truncated to {maxChars} chars; {hint})";
    }

    /// <summary>
    /// A bound that <b>remembers when it bit</b> (#119).
    /// <para>
    /// The bounds themselves are not new — TICKET-210 capped <c>limit ≤ 100</c>, <c>hops ≤ 5</c>,
    /// <c>maxHops ≤ 10</c>. They were applied <i>silently</i>: a caller asking for <c>limit=100000</c> got 100
    /// results and no hint that its request had been reduced, so an agent could reasonably conclude "only 100
    /// nodes matched" when 100 of thousands were returned. Every other cap in this codebase announces itself
    /// (<c>get_source</c>, <c>get_subgraph</c>, <c>generate_capsule</c>); this one lied by omission.
    /// </para>
    /// <para>
    /// The note is emitted <b>only when the clamp actually bites</b>, so the ordinary call stays noise-free —
    /// which was the (unfounded) fear that kept this unshipped.
    /// </para>
    /// </summary>
    public sealed class ClampReport
    {
        private readonly List<string> _notes = new();

        /// <summary>Clamps <paramref name="arg"/> into [<paramref name="min"/>, <paramref name="max"/>], recording any reduction.</summary>
        public int Clamp(JsonNode? arg, string name, int fallback, int min, int max)
        {
            var requested = ReadInt(arg, fallback);
            var clamped = Math.Clamp(requested, min, max);

            // Only report an explicit request we overrode — never a defaulted value, and never a no-op clamp.
            if (arg is not null && requested != clamped)
            {
                _notes.Add(requested > max
                    ? $"{name} clamped to {max} (you requested {requested})"
                    : $"{name} raised to {min} (you requested {requested})");
            }
            return clamped;
        }

        /// <summary>Empty on the common path; a trailing warning line when a bound was actually applied.</summary>
        public string Suffix => _notes.Count == 0 ? "" : $"\n\n⚠ {string.Join("; ", _notes)}";

        /// <summary>Appends <see cref="Suffix"/> to a tool's rendered text.</summary>
        public string Annotate(string text) => text + Suffix;
    }

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

    /// <summary>Reads a double tool argument tolerantly (JSON number or numeric string), else <paramref name="fallback"/>.</summary>
    public static double ReadDouble(JsonNode? value, double fallback)
    {
        if (value is null) return fallback;
        try
        {
            return value.GetValueKind() switch
            {
                JsonValueKind.Number => value.GetValue<double>(),
                JsonValueKind.String => double.TryParse(value.GetValue<string>(),
                    System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : fallback,
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
    /// Resolves a caller-supplied file argument (absolute path, project-relative path, or an <c>@/</c>
    /// handle) to a full path and guarantees it stays INSIDE the project root — the single containment
    /// gate for every filesystem-touching tool. A path that normalizes outside the root (via <c>..</c>,
    /// an absolute path elsewhere, or a different drive) is rejected with an <paramref name="error"/> that
    /// names the allowed root, closing the traversal → index → exfiltrate chain. Returns <c>false</c> and
    /// sets <paramref name="error"/> on rejection; on success <paramref name="fullPath"/> is the normalized,
    /// contained absolute path.
    /// </summary>
    public static bool TryResolveContainedPath(string? raw, string? basePath, out string fullPath, out string? error)
    {
        fullPath = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "Path is required.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(basePath))
        {
            error = "No project root is configured, so no file path can be validated as contained.";
            return false;
        }

        var resolved = FromHandle(raw, basePath);
        if (!System.IO.Path.IsPathRooted(resolved))
        {
            resolved = System.IO.Path.Combine(basePath, resolved);
        }

        string full, rootFull;
        try
        {
            full = System.IO.Path.GetFullPath(resolved);
            rootFull = System.IO.Path.GetFullPath(basePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "The path is not a valid filesystem path.";
            return false;
        }

        // #104: GetFullPath normalizes LEXICALLY — it collapses "..", but it does not follow symlinks. A link
        // inside the tree pointing outside it is therefore lexically contained, passes this gate, and is then
        // read straight through. Resolve both sides to their real locations BEFORE comparing, or the guard is
        // decorative.
        var realFull = ResolveSymlinks(full);
        var realRoot = ResolveSymlinks(rootFull);

        // GetRelativePath returns "." when equal to the root, a "..\\…" prefix when the target escapes it,
        // and a rooted path when on another drive — all of which mean "outside", so admit only paths whose
        // relative form neither starts with ".." nor is itself rooted.
        var rel = System.IO.Path.GetRelativePath(realRoot, realFull);
        if (rel == ".." || rel.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || rel.StartsWith("../", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(rel))
        {
            // Name the CONFIGURED root, not the symlink-resolved one: the caller needs to know what IS
            // allowed (TICKET-209 chose this deliberately — a bare "denied" just makes an agent guess again),
            // and it is the configured root they reason about. Leaking the root's own link target would tell
            // them something they did not ask for and cannot use.
            error = $"Path '{raw}' resolves outside the project root '{rootFull}' — only files within the project may be accessed.";
            return false;
        }

        // Hand back the REAL path, not the lexical one: a caller that then opens it must open the same file
        // this gate approved. Returning the pre-resolution path would re-open the very gap just closed.
        fullPath = realFull;
        return true;
    }

    /// <summary>
    /// Returns <paramref name="full"/> with every symlink on the way resolved to its real target (#104).
    ///
    /// <para>
    /// Resolution is <b>component by component</b>, and that is the whole point. Resolving only the leaf is
    /// not enough: if <c>&lt;root&gt;/docs</c> is a link to <c>/etc</c>, then <c>&lt;root&gt;/docs/passwd</c>
    /// is a perfectly ordinary file — <b>it</b> is not a link, so a leaf-only check finds nothing to resolve
    /// and happily reports the path as contained. The escape hides in the <i>directory</i>, not the file.
    /// </para>
    ///
    /// <para>
    /// Components that do not exist yet are left as-is: there is no link to follow, and a tool may legitimately
    /// name a file it is about to create. A component we cannot stat (permissions) is also left lexical —
    /// failing closed on an unreadable directory would break ordinary use, and the containment check that
    /// follows still runs on whatever we did resolve.
    /// </para>
    /// </summary>
    public static string ResolveSymlinks(string full)
    {
        if (string.IsNullOrEmpty(full)) return full;

        var root = System.IO.Path.GetPathRoot(full) ?? string.Empty;
        var rest = full[root.Length..]
            .Split(new[] { System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar },
                   StringSplitOptions.RemoveEmptyEntries);

        var current = root.Length > 0 ? root : System.IO.Path.DirectorySeparatorChar.ToString();

        foreach (var part in rest)
        {
            current = System.IO.Path.Combine(current, part);
            try
            {
                FileSystemInfo? info = Directory.Exists(current) ? new DirectoryInfo(current)
                                     : File.Exists(current) ? new FileInfo(current)
                                     : null;
                if (info?.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    current = target.FullName;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Cannot stat it — keep the lexical form and let the containment check below judge that.
            }
        }
        return current;
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

    /// <summary>
    /// A tool response whose payload is <b>JSON</b>, plus an optional out-of-band note (#119).
    /// <para>
    /// The note goes in a <b>second content block</b> rather than being appended to the payload. Appending
    /// prose to a JSON document is precisely the bug #117 is about — it stops being parseable. And wrapping
    /// the payload in an envelope to carry the note would change a shape existing callers already parse. A
    /// second block costs neither: <c>content[0].text</c> stays exactly the JSON it was, and the note still
    /// reaches the model.
    /// </para>
    /// </summary>
    public static string SendToolJsonResponse(JsonElement id, string json, string note = "")
    {
        var blocks = string.IsNullOrWhiteSpace(note)
            ? new[] { new { type = "text", text = json } }
            : new[] { new { type = "text", text = json }, new { type = "text", text = note.Trim() } };
        return SendResponse(id, new { content = blocks });
    }

    public static string SendError(JsonElement id, int code, string message) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code, message } });
}
