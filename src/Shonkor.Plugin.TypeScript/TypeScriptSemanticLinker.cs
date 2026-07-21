// Licensed to Shonkor under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;

namespace Shonkor.Plugin.TypeScript;

/// <summary>
/// Whole-program semantic post-processor for TypeScript (#294, epic #296): the plugin's pendant to the
/// in-host <c>SemanticCsharpLinker</c>. After the per-file #293 parse has assembled the graph, this
/// <see cref="IGraphPostProcessor"/> runs once over the whole graph, gathers the typed-TS file set from the
/// <c>JSComponent</c> nodes, and asks a short-lived Node sidecar to build ONE <c>ts.createProgram</c> +
/// <c>getTypeChecker()</c> and resolve the cross-file semantic edges — CALLS, REFERENCES_TYPE, OVERRIDES,
/// IMPLEMENTS_MEMBER, plus cross-file-sharpened EXTENDS/IMPLEMENTS — on NODE IDS (never names/paths).
///
/// <para>The edges are returned <b>additively</b> (the contract of <see cref="IGraphPostProcessor"/>): the
/// host upserts them directly (bypassing the parser's <c>DefaultProvenance</c> stamp) at
/// <see cref="Provenance.Extracted"/>. Because the store keeps the MIN provenance on conflict, an EXTRACTED
/// edge that coincides with #293's INFERRED same-file heritage SHARPENS it automatically — no mutation of
/// phase-1 data. External symbols (node_modules / <c>*.d.ts</c>) have no node and are skipped both in the
/// sidecar and here (a known-id filter), so no edge dangles (AC#4).</para>
///
/// <para>Whole-graph by nature, so it runs on a FULL scan only (the scanner's phase 5.5), never on a
/// single-file reindex — like the C# linker. Incremental relink is the follow-up #318; the sidecar
/// <c>link</c> request is already file-subset-capable so that work is not foreclosed. Overload precision is
/// the follow-up #321 (edges land at method granularity — an overload set is one TS symbol).</para>
/// </summary>
public sealed class TypeScriptSemanticLinker : IGraphPostProcessor, IPluginInitializable
{
    private const string ComponentType = "JSComponent";

    /// <summary>The parser node types whose ids the linker's edges may point at — the dangling guard (AC#4).</summary>
    private static readonly string[] ParserNodeTypes =
    {
        "JSComponent", "Class", "Interface", "Function", "Enum", "TypeAlias", "Method", "Property"
    };

    /// <summary>Extensions treated as typed TS: only these files ORIGINATE EXTRACTED edges (untyped JS -> #293/#295).</summary>
    private static readonly HashSet<string> TypedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".ts", ".tsx" };

    private readonly string _scriptPath;
    private readonly SidecarSettings _settings;
    private readonly TimeSpan _timeout;

    private ILogger _logger = NullLogger.Instance;

    public TypeScriptSemanticLinker()
    {
        var pluginDirectory = ResolvePluginDirectory();
        _scriptPath = Path.Combine(pluginDirectory, "sidecar", "index.js");
        _settings = SidecarSettings.Load(pluginDirectory);
        _timeout = ResolveLinkTimeout(_settings.ResolveTimeout());
    }

    /// <summary>
    /// Test-only constructor: injects explicit settings (Node path / timeout) so a test can drive the linker
    /// deterministically. The sidecar script is still resolved from the plugin folder.
    /// </summary>
    internal TypeScriptSemanticLinker(SidecarSettings settings, string? pluginDirectoryOverride = null)
    {
        var pluginDirectory = pluginDirectoryOverride ?? ResolvePluginDirectory();
        _scriptPath = Path.Combine(pluginDirectory, "sidecar", "index.js");
        _settings = settings;
        _timeout = ResolveLinkTimeout(settings.ResolveTimeout());
    }

    /// <inheritdoc />
    public string Name => "typescript.semantic-linker";

    /// <inheritdoc />
    public void Initialize(IPluginHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _logger = host.Logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public Task<GraphEnrichment> ProcessAsync(IGraphView graph) => ProcessAsync(graph, GraphPostProcessorContext.Empty);

    /// <inheritdoc />
    public async Task<GraphEnrichment> ProcessAsync(IGraphView graph, GraphPostProcessorContext context)
    {
        ArgumentNullException.ThrowIfNull(graph);

        // 1. The typed-TS file set: distinct FilePath of every JSComponent node, .ts/.tsx only. Untyped JS is
        //    intentionally NOT linked here (no EXTRACTED without type info) — #293/#295 own it.
        var components = await graph.NodesByTypeAsync(ComponentType).ConfigureAwait(false);
        var files = components
            .Select(c => c.FilePath)
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .Where(p => TypedExtensions.Contains(Path.GetExtension(p)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0) return GraphEnrichment.Empty;

        // 2. The set of ids the parser actually emitted — an edge may only point at one of these (AC#4).
        var knownIds = await CollectKnownIdsAsync(graph).ConfigureAwait(false);

        // 3. Resolve the sidecar + node executable (own short-lived client — never the parser's instance).
        if (!File.Exists(_scriptPath))
        {
            return Diagnostic(DiagnosticSeverity.Warning, $"Sidecar script not found at '{_scriptPath}'; skipped TS semantic linking.");
        }
        var nodePath = NodeDiscovery.Discover(_settings.NodePath, out var reason);
        if (nodePath is null)
        {
            _logger.LogWarning("TypeScript semantic linker: Node unavailable ({Reason}); cross-file semantic edges skipped.", reason);
            return Diagnostic(DiagnosticSeverity.Info, $"Node unavailable ({reason}); TS cross-file semantic edges skipped.");
        }

        NodeSidecarClient client;
        try
        {
            client = NodeSidecarClient.Start(nodePath, _scriptPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("TypeScript semantic linker: failed to start sidecar ({Message}); edges skipped.", ex.Message);
            return Diagnostic(DiagnosticSeverity.Warning, $"Failed to start sidecar ({ex.Message}); TS cross-file semantic edges skipped.");
        }

        await using (client)
        {
            SidecarResponse response;
            try
            {
                response = await client.SendLinkAsync(files, CommonDirectory(files), _timeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning(
                    "TypeScript semantic linker: whole-program link timed out after {TimeoutSeconds}s; edges skipped.",
                    _timeout.TotalSeconds);
                return Diagnostic(DiagnosticSeverity.Warning, $"Whole-program link timed out after {_timeout.TotalSeconds:0}s; TS cross-file semantic edges skipped.");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                _logger.LogWarning("TypeScript semantic linker: sidecar transport error ({Message}); edges skipped.", ex.Message);
                return Diagnostic(DiagnosticSeverity.Warning, $"Sidecar transport error ({ex.Message}); TS cross-file semantic edges skipped.");
            }

            return BuildEnrichment(response, knownIds);
        }
    }

    private GraphEnrichment BuildEnrichment(SidecarResponse response, HashSet<string> knownIds)
    {
        var edges = new List<GraphEdge>(response.Edges.Count);
        var skipped = 0;
        foreach (var e in response.Edges)
        {
            // Dangling guard (AC#4): drop any edge whose endpoints are not both real, parser-emitted nodes.
            // The sidecar already skips external/metadata symbols; this is the belt-and-suspenders backstop.
            if (!knownIds.Contains(e.SourceId) || !knownIds.Contains(e.TargetId))
            {
                skipped++;
                continue;
            }

            edges.Add(new GraphEdge
            {
                SourceId = e.SourceId,
                TargetId = e.TargetId,
                Relationship = e.Relationship,
                // Type-checker-resolved -> EXTRACTED (AC#5). The store's MIN-provenance upsert sharpens any
                // coinciding #293 INFERRED heritage edge to EXTRACTED (AC#3) without mutating phase-1 data.
                Provenance = Provenance.Extracted
            });
        }

        var diagnostics = new List<GraphDiagnostic>();
        foreach (var d in response.Diagnostics)
        {
            var severity = d.Severity?.ToLowerInvariant() switch
            {
                "error" => DiagnosticSeverity.Error,
                "warning" => DiagnosticSeverity.Warning,
                _ => DiagnosticSeverity.Info
            };
            diagnostics.Add(new GraphDiagnostic(Name, severity, d.Message));
        }

        _logger.LogInformation(
            "TypeScript semantic linker: emitted {EdgeCount} cross-file edges ({Skipped} dangling skipped).",
            edges.Count, skipped);

        return new GraphEnrichment(Array.Empty<GraphNode>(), edges, diagnostics);
    }

    private async Task<HashSet<string>> CollectKnownIdsAsync(IGraphView graph)
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in ParserNodeTypes)
        {
            foreach (var node in await graph.NodesByTypeAsync(type).ConfigureAwait(false))
            {
                known.Add(node.Id);
            }
        }
        return known;
    }

    private GraphEnrichment Diagnostic(DiagnosticSeverity severity, string message) =>
        new(Array.Empty<GraphNode>(), Array.Empty<GraphEdge>(), new[] { new GraphDiagnostic(Name, severity, message) });

    /// <summary>
    /// The deepest directory that contains every file — passed as the sidecar's <c>projectDir</c> so its
    /// tsconfig lookup (which walks UP) finds the project's config. Falls back to the first file's directory.
    /// </summary>
    private static string CommonDirectory(IReadOnlyList<string> files)
    {
        var firstDir = Path.GetDirectoryName(files[0]) ?? files[0];
        if (files.Count == 1) return firstDir;

        var prefix = firstDir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var prefixLen = prefix.Length;
        for (var i = 1; i < files.Count; i++)
        {
            var dir = (Path.GetDirectoryName(files[i]) ?? files[i])
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var common = 0;
            while (common < prefixLen && common < dir.Length &&
                   string.Equals(prefix[common], dir[common], StringComparison.OrdinalIgnoreCase))
            {
                common++;
            }
            prefixLen = common;
            if (prefixLen == 0) break;
        }

        return prefixLen == 0 ? firstDir : string.Join(Path.DirectorySeparatorChar, prefix.Take(prefixLen));
    }

    /// <summary>
    /// A whole-program pass is heavier than a single-file parse, so give it a generous floor while still
    /// honouring a larger configured budget.
    /// </summary>
    private static TimeSpan ResolveLinkTimeout(TimeSpan parseTimeout) =>
        parseTimeout.TotalSeconds > 120 ? parseTimeout : TimeSpan.FromSeconds(120);

    private static string ResolvePluginDirectory()
    {
        try
        {
            var location = typeof(TypeScriptSemanticLinker).Assembly.Location;
            var dir = Path.GetDirectoryName(location);
            if (!string.IsNullOrEmpty(dir)) return dir;
        }
        catch
        {
            // Single-file / trimmed hosts can throw on Location — fall through.
        }
        return AppContext.BaseDirectory;
    }
}
