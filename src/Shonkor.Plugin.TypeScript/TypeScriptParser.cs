// Licensed to Shonkor under the MIT License.

using System.Collections.Frozen;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;

namespace Shonkor.Plugin.TypeScript;

/// <summary>
/// The JS/TS base parser (#292, epic #296): a thin <see cref="IFileParser"/> adapter over a Node sidecar
/// that runs the real TypeScript Compiler API. It replaces the former in-host Esprima parser, and carries
/// that Esprima parse as a PRIVATE fallback so JS/TS indexing degrades — visibly, with a diagnostic —
/// rather than stopping when Node is unavailable or a parse hangs.
///
/// <para>One sidecar per scan: the process is started lazily on the first parse and reused across every
/// file; the plugin loader disposes this parser at scan end (#306), which tears the process down.</para>
/// </summary>
public sealed class TypeScriptParser : IFileParser, IPluginInitializable, IAsyncDisposable
{
    /// <summary>Recycle the sidecar process after this many consecutive timeouts (then a fresh one is started).</summary>
    private const int TimeoutRecycleThreshold = 3;

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly string _pluginDirectory;
    private readonly string _scriptPath;
    private readonly SidecarSettings _settings;
    private readonly TimeSpan _timeout;

    private ILogger _logger = NullLogger.Instance;
    private NodeSidecarClient? _client;
    private bool _discoveryDone;
    private string? _nodePath;
    private bool _nodeUnavailableLogged;
    private int _consecutiveTimeouts;
    private volatile bool _disposed;

    public TypeScriptParser()
    {
        _pluginDirectory = ResolvePluginDirectory();
        _scriptPath = Path.Combine(_pluginDirectory, "sidecar", "index.js");
        _settings = SidecarSettings.Load(_pluginDirectory);
        _timeout = _settings.ResolveTimeout();
    }

    /// <summary>
    /// Test-only constructor: injects explicit settings (Node path / timeout) so a test can exercise
    /// degradation and timeout paths deterministically without touching the on-disk settings file. The
    /// sidecar script is still resolved from the plugin folder.
    /// </summary>
    internal TypeScriptParser(SidecarSettings settings, string? pluginDirectoryOverride = null)
    {
        _pluginDirectory = pluginDirectoryOverride ?? ResolvePluginDirectory();
        _scriptPath = Path.Combine(_pluginDirectory, "sidecar", "index.js");
        _settings = settings;
        _timeout = settings.ResolveTimeout();
    }

    /// <inheritdoc />
    public IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".ts", ".tsx", ".js", ".jsx" }.ToFrozenSet();

    /// <inheritdoc />
    /// <remarks>Syntactic parse without cross-file semantic resolution — heuristic, so never Extracted (#294 escalates).</remarks>
    public Provenance DefaultProvenance => Provenance.Inferred;

    /// <inheritdoc />
    /// <remarks>
    /// The module-level <c>JSComponent</c> (kept for parity + cross-tech coexistence) plus the #293 symbol
    /// nodes the sidecar now emits from the real TS AST — analogous to <c>RoslynAstParser</c>'s type/member
    /// node types. Properties (Property) are activated on demand; the coarser symbols are visible by default.
    /// </remarks>
    public IReadOnlyList<NodeTypeDescriptor> NodeTypeDescriptors { get; } = new[]
    {
        new NodeTypeDescriptor("JSComponent", "Code", true),
        new NodeTypeDescriptor("Class", "Code", true),
        new NodeTypeDescriptor("Interface", "Code", true),
        new NodeTypeDescriptor("Function", "Code", true),
        new NodeTypeDescriptor("Enum", "Code", true),
        new NodeTypeDescriptor("TypeAlias", "Code", true),
        new NodeTypeDescriptor("Method", "Code", true),
        new NodeTypeDescriptor("Property", "Code", false)
    };

    /// <inheritdoc />
    public void Initialize(IPluginHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _logger = host.Logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)> ParseAsync(
        string filePath, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(content);

        var client = await EnsureClientAsync().ConfigureAwait(false);
        if (client == null)
        {
            // Node unavailable / sidecar could not start — degrade to the prior Esprima behaviour (AC#3).
            return Fallback(filePath, content, "Node sidecar unavailable; using Esprima fallback.");
        }

        try
        {
            var response = await client.SendAsync(filePath, content, _timeout).ConfigureAwait(false);
            _consecutiveTimeouts = 0;
            return MapResponse(filePath, response);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "TypeScript sidecar timed out after {TimeoutSeconds}s parsing {FilePath}; degraded to Esprima fallback.",
                _timeout.TotalSeconds, filePath);
            await OnTimeoutAsync().ConfigureAwait(false);
            return Fallback(filePath, content, diagnostic: null); // Diagnostic already logged above.
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // Transport died mid-request: recycle and fall back so the scan continues (AC#3).
            await RecycleAsync().ConfigureAwait(false);
            return Fallback(filePath, content, $"Node sidecar transport error ({ex.Message}); using Esprima fallback.");
        }
    }

    private async Task OnTimeoutAsync()
    {
        if (Interlocked.Increment(ref _consecutiveTimeouts) >= TimeoutRecycleThreshold)
        {
            _logger.LogWarning(
                "TypeScript sidecar hit {Count} consecutive timeouts; recycling the process.", _consecutiveTimeouts);
            await RecycleAsync().ConfigureAwait(false);
            _consecutiveTimeouts = 0;
        }
    }

    private async Task RecycleAsync()
    {
        await _initLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_client != null)
            {
                await _client.DisposeAsync().ConfigureAwait(false);
                _client = null;
            }
            // Node itself is still considered available (discovery cached in _nodePath); the next parse
            // lazily starts a fresh process.
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task<NodeSidecarClient?> EnsureClientAsync()
    {
        var existing = _client;
        if (existing is { IsAlive: true }) return existing;

        await _initLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return null;
            if (_client is { IsAlive: true }) return _client;

            // A dead client from a prior failure: clear it before re-evaluating.
            if (_client != null)
            {
                await _client.DisposeAsync().ConfigureAwait(false);
                _client = null;
            }

            if (!_discoveryDone)
            {
                _discoveryDone = true;
                if (!File.Exists(_scriptPath))
                {
                    _nodePath = null;
                    LogNodeUnavailableOnce($"Sidecar script not found at '{_scriptPath}'.");
                }
                else
                {
                    _nodePath = NodeDiscovery.Discover(_settings.NodePath, out var reason);
                    if (_nodePath == null) LogNodeUnavailableOnce(reason);
                }
            }

            if (_nodePath == null) return null; // Permanently unavailable for this scan → Esprima path.

            try
            {
                _client = NodeSidecarClient.Start(_nodePath, _scriptPath);
                return _client;
            }
            catch (Exception ex)
            {
                LogNodeUnavailableOnce($"Failed to start node sidecar: {ex.Message}");
                _nodePath = null; // Do not thrash restart attempts for the rest of the scan.
                return null;
            }
        }
        finally
        {
            _initLock.Release();
        }
    }

    private void LogNodeUnavailableOnce(string reason)
    {
        if (_nodeUnavailableLogged) return;
        _nodeUnavailableLogged = true;
        _logger.LogWarning(
            "TypeScript Node sidecar unavailable ({Reason}); JS/TS files will be parsed with the Esprima fallback.",
            reason);
    }

    private (IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges) MapResponse(
        string filePath, SidecarResponse response)
    {
        foreach (var d in response.Diagnostics)
        {
            LogDiagnostic(filePath, d);
        }

        var nodes = new List<GraphNode>(response.Nodes.Count);
        foreach (var n in response.Nodes)
        {
            nodes.Add(new GraphNode
            {
                Id = n.Id,
                Name = n.Name,
                Type = string.IsNullOrEmpty(n.Type) ? "JSComponent" : n.Type,
                FilePath = n.FilePath ?? filePath,
                // #293: symbol nodes carry 1-based line provenance; the module node leaves these null.
                StartLine = n.StartLine,
                EndLine = n.EndLine,
                Properties = n.Properties ?? new Dictionary<string, string>()
            });
        }

        var edges = new List<GraphEdge>(response.Edges.Count);
        foreach (var e in response.Edges)
        {
            edges.Add(new GraphEdge
            {
                SourceId = e.SourceId,
                TargetId = e.TargetId,
                Relationship = e.Relationship,
                // Provenance is stamped by the host from DefaultProvenance; leave the default here.
                Properties = e.Properties ?? new Dictionary<string, string>()
            });
        }

        return (nodes, edges);
    }

    private void LogDiagnostic(string filePath, SidecarDiagnostic d)
    {
        var where = d.Line is > 0 ? $"{filePath}:{d.Line}" : filePath;
        switch (d.Severity?.ToLowerInvariant())
        {
            case "info":
                _logger.LogInformation("TypeScript sidecar: {Message} ({Where})", d.Message, where);
                break;
            case "warning":
                _logger.LogWarning("TypeScript sidecar diagnostic: {Message} ({Where})", d.Message, where);
                break;
            default:
                // Parse errors are SURFACED, never swallowed (AC#2).
                _logger.LogWarning(
                    "TypeScript parse diagnostic (TS{Code}): {Message} ({Where})", d.Code, d.Message, where);
                break;
        }
    }

    private (IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges) Fallback(
        string filePath, string content, string? diagnostic)
    {
        if (diagnostic != null)
        {
            _logger.LogWarning("TypeScript parser: {Diagnostic} ({FilePath})", diagnostic, filePath);
        }
        var (nodes, edges) = EsprimaFallbackParser.Parse(filePath, content);
        return (nodes, edges);
    }

    private static string ResolvePluginDirectory()
    {
        // The plugin's own folder — where sidecar/ and sidecar.settings.json live. When loaded via an
        // isolated ALC (production) or referenced directly (tests), the assembly location is the plugin
        // folder; AppContext.BaseDirectory (the host's dir) is only a last-resort fallback.
        try
        {
            var location = typeof(TypeScriptParser).Assembly.Location;
            var dir = Path.GetDirectoryName(location);
            if (!string.IsNullOrEmpty(dir)) return dir;
        }
        catch
        {
            // Single-file / trimmed hosts can throw on Location — fall through.
        }
        return AppContext.BaseDirectory;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _initLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_client != null)
            {
                await _client.DisposeAsync().ConfigureAwait(false);
                _client = null;
            }
        }
        finally
        {
            _initLock.Release();
        }
        _initLock.Dispose();
    }
}
