// Licensed to Shonkor under the MIT License.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using StreamJsonRpc;

namespace Shonkor.Bench;

/// <summary>
/// A minimal LSP client for the spike in #467: spawns a language server on stdio, speaks JSON-RPC with
/// <c>Content-Length</c> headers (<see cref="HeaderDelimitedMessageHandler"/>), answers the handful of
/// server→client requests Roslyn insists on, and exposes typed wrappers for the eight requests the diff
/// needs. Every request is stopwatch-marked so the report can show <c>t_init</c>, <c>t_ready</c> and
/// per-request <c>t_warm</c> without a second instrumentation layer.
///
/// <para>
/// Lives in Bench on purpose: whether a language server belongs anywhere near Core is the decision this
/// spike informs, not one it makes. <c>McpProxyClient</c> (newline-delimited) is deliberately not reused —
/// LSP is header-delimited and the two framings share nothing but the word "JSON".
/// </para>
/// </summary>
internal sealed class LspClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly Process _process;
    private readonly JsonRpc _rpc;
    private readonly Stopwatch _clock;
    private readonly TextWriter _log;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<string> _openedFiles = new(Shonkor.Core.Services.FilePaths.Comparer);
    private readonly List<string> _dynamicRegistrations = [];
    private readonly List<string> _loadErrors = [];
    private bool _disposed;

    private LspClient(Process process, Stopwatch clock, TextWriter log)
    {
        _process = process;
        _clock = clock;
        _log = log;
        var formatter = new SystemTextJsonFormatter();
        var handler = new HeaderDelimitedMessageHandler(process.StandardInput.BaseStream, process.StandardOutput.BaseStream, formatter);
        _rpc = new JsonRpc(handler);
        _rpc.AddLocalRpcTarget(new ServerCallbacks(this), new JsonRpcTargetOptions { NotifyClientOfEvents = false });
        _rpc.Disconnected += (_, e) => Log($"[rpc] disconnected: {e.Reason} {e.Description}");
        // Binding failures on incoming notifications are otherwise invisible — they surface only here.
        _rpc.TraceSource.Switch.Level = SourceLevels.Warning;
        _rpc.TraceSource.Listeners.Add(new TextWriterTraceListener(log));
        _rpc.StartListening();
    }

    /// <summary>Elapsed time from process spawn to the <c>initialize</c> response.</summary>
    public TimeSpan? InitElapsed { get; private set; }

    /// <summary>Elapsed time from process spawn to <c>workspace/projectInitializationComplete</c>.</summary>
    public TimeSpan? ReadyElapsed { get; private set; }

    /// <summary>The raw <c>initialize</c> result, dumped verbatim into the spike note (Step 0).</summary>
    public JsonElement InitializeResult { get; private set; }

    /// <summary>Methods the server registered dynamically via <c>client/registerCapability</c>.</summary>
    public IReadOnlyList<string> DynamicRegistrations => _dynamicRegistrations;

    /// <summary>
    /// <c>window/logMessage</c> entries that report a project the server could not load
    /// (<see cref="LspDiff.IsProjectLoadError"/>). Loading continues without the project, so this is the only
    /// trace of a run whose numbers are wrong.
    /// </summary>
    public IReadOnlyList<string> LoadErrors { get { lock (_loadErrors) return _loadErrors.ToList(); } }

    /// <summary>Stopwatch marks of every request issued after the server was ready.</summary>
    public List<LspTiming> Timings { get; } = [];

    /// <summary>Whether readiness has been signalled by the server.</summary>
    public bool IsReady => _ready.Task.IsCompleted;

    /// <summary>
    /// Spawns <paramref name="commandLine"/> (first token = executable, rest = arguments) with redirected
    /// stdio and starts the JSON-RPC session. The stopwatch starts immediately before <c>Process.Start</c>
    /// so <c>t_init</c>/<c>t_ready</c> include the server's own startup.
    /// </summary>
    public static LspClient Start(string commandLine, TextWriter log)
    {
        var (file, arguments) = SplitCommandLine(commandLine);
        var psi = new ProcessStartInfo
        {
            FileName = file,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        var process = new Process { StartInfo = psi };
        var clock = Stopwatch.StartNew();
        process.Start();
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) log.WriteLine($"[stderr] {e.Data}"); };
        process.BeginErrorReadLine();
        return new LspClient(process, clock, log);
    }

    /// <summary>LSP 3.17 <c>initialize</c> + <c>initialized</c>. Hierarchical document symbols are mandatory — without them there is no <c>selectionRange</c> to anchor on.</summary>
    public async Task<JsonElement> InitializeAsync(string rootDir, CancellationToken ct)
    {
        var rootUri = ToUri(rootDir);
        var parameters = new
        {
            processId = Environment.ProcessId,
            rootUri,
            capabilities = new
            {
                textDocument = new
                {
                    documentSymbol = new { hierarchicalDocumentSymbolSupport = true },
                    callHierarchy = new { },
                    implementation = new { },
                    references = new { }
                },
                workspace = new { configuration = true, workspaceFolders = true }
            },
            workspaceFolders = new[] { new { uri = rootUri, name = Path.GetFileName(rootDir.TrimEnd(Path.DirectorySeparatorChar)) } }
        };
        var result = await _rpc.InvokeWithParameterObjectAsync<JsonElement>("initialize", parameters, ct).ConfigureAwait(false);
        InitElapsed = _clock.Elapsed;
        InitializeResult = result.Clone();
        await _rpc.NotifyWithParameterObjectAsync("initialized", new { }).ConfigureAwait(false);
        return InitializeResult;
    }

    /// <summary>Roslyn's non-standard <c>solution/open</c> notification.</summary>
    public Task OpenSolutionAsync(string solutionPath) =>
        _rpc.NotifyWithParameterObjectAsync("solution/open", new { solution = ToUri(solutionPath) });

    /// <summary>Roslyn's non-standard <c>project/open</c> notification — the fallback when a solution file is not understood.</summary>
    public Task OpenProjectsAsync(IEnumerable<string> projectPaths) =>
        _rpc.NotifyWithParameterObjectAsync("project/open", new { projects = projectPaths.Select(ToUri).ToArray() });

    /// <summary>Waits for <c>workspace/projectInitializationComplete</c>; false on timeout.</summary>
    public async Task<bool> WaitForReadyAsync(TimeSpan timeout, CancellationToken ct)
    {
        var completed = await Task.WhenAny(_ready.Task, Task.Delay(timeout, ct)).ConfigureAwait(false);
        return completed == _ready.Task;
    }

    /// <summary>Marks readiness from the fallback probe (first non-empty <c>references</c> on a control symbol).</summary>
    public void MarkReadyByFallback() => MarkReady("fallback probe");

    private void MarkReady(string via)
    {
        if (_ready.TrySetResult()) ReadyElapsed = _clock.Elapsed;
        Log($"[client] ready via {via} at {_clock.Elapsed.TotalSeconds:F1}s");
    }

    /// <summary><c>textDocument/didOpen</c> with the raw file text — sent once per file.</summary>
    public async Task DidOpenAsync(string file)
    {
        if (!_openedFiles.Add(file)) return;
        var text = await File.ReadAllTextAsync(file).ConfigureAwait(false);
        await _rpc.NotifyWithParameterObjectAsync("textDocument/didOpen",
            new { textDocument = new { uri = ToUri(file), languageId = "csharp", version = 1, text } }).ConfigureAwait(false);
    }

    /// <summary><c>textDocument/documentSymbol</c>, flattened recursively.</summary>
    public async Task<IReadOnlyList<DocumentSymbol>> DocumentSymbolsAsync(string file, CancellationToken ct)
    {
        var first = !_openedFiles.Contains(file);
        await DidOpenAsync(file).ConfigureAwait(false);
        var result = await TimedAsync("textDocument/documentSymbol", file, first,
            () => _rpc.InvokeWithParameterObjectAsync<JsonElement>("textDocument/documentSymbol",
                new { textDocument = new { uri = ToUri(file) } }, ct)).ConfigureAwait(false);
        if (result.ValueKind != JsonValueKind.Array) return [];
        var top = JsonSerializer.Deserialize<List<DocumentSymbol>>(result.GetRawText(), ReadOptions) ?? [];
        var flat = new List<DocumentSymbol>();
        Flatten(top, flat);
        return flat;
    }

    /// <summary><c>textDocument/references</c> without the declaration.</summary>
    public async Task<IReadOnlyList<LspLocation>> ReferencesAsync(string file, LspPosition position, CancellationToken ct)
    {
        var result = await TimedAsync("textDocument/references", file, false,
            () => _rpc.InvokeWithParameterObjectAsync<JsonElement>("textDocument/references",
                new { textDocument = new { uri = ToUri(file) }, position, context = new { includeDeclaration = false } }, ct)).ConfigureAwait(false);
        return ParseLocations(result);
    }

    /// <summary><c>textDocument/prepareCallHierarchy</c> → <c>callHierarchy/incomingCalls</c>; returns each caller's <c>selectionRange.start</c> as a location.</summary>
    public async Task<IReadOnlyList<LspLocation>> IncomingCallersAsync(string file, LspPosition position, CancellationToken ct)
    {
        var items = await TimedAsync("textDocument/prepareCallHierarchy", file, false,
            () => _rpc.InvokeWithParameterObjectAsync<JsonElement>("textDocument/prepareCallHierarchy",
                new { textDocument = new { uri = ToUri(file) }, position }, ct)).ConfigureAwait(false);
        if (items.ValueKind != JsonValueKind.Array) return [];

        var callers = new List<LspLocation>();
        foreach (var item in items.EnumerateArray())
        {
            // The item is echoed back verbatim: Roslyn stashes resolution data in it, and a re-typed copy
            // would lose that.
            var calls = await TimedAsync("callHierarchy/incomingCalls", file, false,
                () => _rpc.InvokeWithParameterObjectAsync<JsonElement>("callHierarchy/incomingCalls", new { item }, ct)).ConfigureAwait(false);
            if (calls.ValueKind != JsonValueKind.Array) continue;
            foreach (var call in calls.EnumerateArray())
            {
                if (!call.TryGetProperty("from", out var from)) continue;
                var caller = JsonSerializer.Deserialize<CallHierarchyItem>(from.GetRawText(), ReadOptions);
                if (caller?.Uri is null || caller.SelectionRange is null) continue;
                // fromRanges are the call SITES inside the caller — kept so a gap can be told apart as an
                // implicit call (`using` disposal) rather than a missing edge.
                var sites = call.TryGetProperty("fromRanges", out var fr) && fr.ValueKind == JsonValueKind.Array
                    ? JsonSerializer.Deserialize<List<LspRange>>(fr.GetRawText(), ReadOptions) ?? []
                    : [];
                callers.Add(new LspLocation { Uri = caller.Uri, Range = caller.SelectionRange, Sites = sites });
            }
        }
        return callers;
    }

    /// <summary><c>textDocument/implementation</c>; tolerant of <c>Location[]</c>, <c>LocationLink[]</c> and a single <c>Location</c>.</summary>
    public async Task<IReadOnlyList<LspLocation>> ImplementationsAsync(string file, LspPosition position, CancellationToken ct)
    {
        var result = await TimedAsync("textDocument/implementation", file, false,
            () => _rpc.InvokeWithParameterObjectAsync<JsonElement>("textDocument/implementation",
                new { textDocument = new { uri = ToUri(file) }, position }, ct)).ConfigureAwait(false);
        return ParseLocations(result);
    }

    /// <summary><c>shutdown</c> → <c>exit</c> → wait 5 s → kill the whole tree. Kill is unconditional in the finally: a leaked BuildHost is the classic LSP-spike failure.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (!_process.HasExited)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    await _rpc.InvokeWithCancellationAsync("shutdown", cancellationToken: cts.Token).ConfigureAwait(false);
                    await _rpc.NotifyAsync("exit").ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or RemoteInvocationException or ConnectionLostException or ObjectDisposedException or IOException)
                {
                    Log($"[client] shutdown handshake failed ({ex.GetType().Name}: {ex.Message}) — killing");
                }
                _process.WaitForExit(5000);
            }
        }
        finally
        {
            if (!_process.HasExited)
            {
                try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* raced with exit */ }
            }
            _rpc.Dispose();
            _process.Dispose();
        }
    }

    // ---- server → client ------------------------------------------------------------------------------------

    /// <summary>
    /// The requests a headless Roslyn issues to its client. Each MUST be answered — an unanswered
    /// <c>workspace/configuration</c> stalls project loading silently, which is indistinguishable from a
    /// slow load until the timeout fires.
    /// </summary>
    private sealed class ServerCallbacks(LspClient owner)
    {
        [JsonRpcMethod("workspace/configuration", UseSingleObjectParameterDeserialization = true)]
        public object?[] Configuration(JsonElement parameters)
        {
            var count = parameters.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array ? items.GetArrayLength() : 0;
            return new object?[count];
        }

        [JsonRpcMethod("client/registerCapability", UseSingleObjectParameterDeserialization = true)]
        public object? RegisterCapability(JsonElement parameters)
        {
            if (parameters.TryGetProperty("registrations", out var regs) && regs.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in regs.EnumerateArray())
                    if (r.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String) owner._dynamicRegistrations.Add(m.GetString()!);
            }
            return null;
        }

        [JsonRpcMethod("client/unregisterCapability", UseSingleObjectParameterDeserialization = true)]
        public object? UnregisterCapability(JsonElement parameters) => null;

        [JsonRpcMethod("window/workDoneProgress/create", UseSingleObjectParameterDeserialization = true)]
        public object? CreateProgress(JsonElement parameters) => null;

        [JsonRpcMethod("workspace/_roslyn_projectNeedsRestore", UseSingleObjectParameterDeserialization = true)]
        public object? ProjectNeedsRestore(JsonElement parameters)
        {
            owner.Log($"[server] workspace/_roslyn_projectNeedsRestore {parameters.GetRawText()}");
            return null;
        }

        [JsonRpcMethod("workspace/_roslyn_projectHasUnresolvedDependencies", UseSingleObjectParameterDeserialization = true)]
        public object? ProjectHasUnresolvedDependencies(JsonElement parameters) => null;

        [JsonRpcMethod("workspace/diagnostic/refresh", UseSingleObjectParameterDeserialization = true)]
        public object? DiagnosticRefresh(JsonElement parameters) => null;

        [JsonRpcMethod("workspace/semanticTokens/refresh", UseSingleObjectParameterDeserialization = true)]
        public object? SemanticTokensRefresh(JsonElement parameters) => null;

        [JsonRpcMethod("workspace/inlayHint/refresh", UseSingleObjectParameterDeserialization = true)]
        public object? InlayHintRefresh(JsonElement parameters) => null;

        [JsonRpcMethod("workspace/codeLens/refresh", UseSingleObjectParameterDeserialization = true)]
        public object? CodeLensRefresh(JsonElement parameters) => null;

        [JsonRpcMethod("window/_roslyn_showToast", UseSingleObjectParameterDeserialization = true)]
        public object? ShowToast(JsonElement parameters)
        {
            owner.Log($"[server] _roslyn_showToast {parameters.GetRawText()}");
            return null;
        }

        [JsonRpcMethod("window/showMessageRequest", UseSingleObjectParameterDeserialization = true)]
        public object? ShowMessageRequest(JsonElement parameters)
        {
            owner.Log($"[server] showMessageRequest {parameters.GetRawText()}");
            return null;
        }

        // Roslyn sends this notification WITHOUT params. A handler that demands a parameter object never
        // matches, the notification is dropped silently, and "the server never became ready" is the wrong
        // conclusion — measured: all projects loaded at 5,5 s, readiness never observed. Both shapes are bound.
        [JsonRpcMethod("workspace/projectInitializationComplete")]
        public void ProjectInitializationComplete() => owner.MarkReady("workspace/projectInitializationComplete");

        [JsonRpcMethod("workspace/projectInitializationComplete", UseSingleObjectParameterDeserialization = true)]
        public void ProjectInitializationComplete(JsonElement parameters) => owner.MarkReady("workspace/projectInitializationComplete (with params)");

        [JsonRpcMethod("window/logMessage", UseSingleObjectParameterDeserialization = true)]
        public void LogMessage(JsonElement parameters)
        {
            var message = parameters.TryGetProperty("message", out var m) ? m.GetString() : parameters.GetRawText();
            owner.Log($"[server:log] {message}");
            if (LspDiff.IsProjectLoadError(message))
                lock (owner._loadErrors) owner._loadErrors.Add(message!);
        }

        [JsonRpcMethod("window/showMessage", UseSingleObjectParameterDeserialization = true)]
        public void ShowMessage(JsonElement parameters) => owner.Log($"[server:show] {parameters.GetRawText()}");

        [JsonRpcMethod("$/progress", UseSingleObjectParameterDeserialization = true)]
        public void Progress(JsonElement parameters) => owner.Log($"[server:progress] {parameters.GetRawText()}");

        [JsonRpcMethod("textDocument/publishDiagnostics", UseSingleObjectParameterDeserialization = true)]
        public void PublishDiagnostics(JsonElement parameters)
        {
            var uri = parameters.TryGetProperty("uri", out var u) ? u.GetString() : "?";
            var n = parameters.TryGetProperty("diagnostics", out var d) && d.ValueKind == JsonValueKind.Array ? d.GetArrayLength() : 0;
            owner.Log($"[server:diagnostics] {uri}: {n}");
        }

        [JsonRpcMethod("telemetry/event", UseSingleObjectParameterDeserialization = true)]
        public void Telemetry(JsonElement parameters) { }
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private async Task<T> TimedAsync<T>(string method, string file, bool firstForFile, Func<Task<T>> request)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            return await request().ConfigureAwait(false);
        }
        finally
        {
            sw.Stop();
            if (IsReady) Timings.Add(new LspTiming(method, file, sw.Elapsed.TotalMilliseconds, firstForFile));
        }
    }

    private void Log(string line)
    {
        lock (_log) _log.WriteLine($"{_clock.Elapsed.TotalSeconds,8:F2}s {line}");
    }

    internal static string ToUri(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    private static void Flatten(IEnumerable<DocumentSymbol> symbols, List<DocumentSymbol> into)
    {
        foreach (var s in symbols)
        {
            into.Add(s);
            if (s.Children is { Count: > 0 }) Flatten(s.Children, into);
        }
    }

    /// <summary><c>Location</c>, <c>Location[]</c> or <c>LocationLink[]</c> — servers differ, the diff should not.</summary>
    internal static IReadOnlyList<LspLocation> ParseLocations(JsonElement result)
    {
        var list = new List<LspLocation>();
        if (result.ValueKind == JsonValueKind.Object) AddLocation(result, list);
        else if (result.ValueKind == JsonValueKind.Array)
            foreach (var e in result.EnumerateArray()) AddLocation(e, list);
        return list;

        static void AddLocation(JsonElement e, List<LspLocation> into)
        {
            if (e.ValueKind != JsonValueKind.Object) return;
            if (e.TryGetProperty("targetUri", out _))
            {
                var link = JsonSerializer.Deserialize<LspLocationLink>(e.GetRawText(), ReadOptions);
                var range = link?.TargetSelectionRange ?? link?.TargetRange;
                if (link?.TargetUri is not null && range is not null) into.Add(new LspLocation { Uri = link.TargetUri, Range = range });
            }
            else
            {
                var loc = JsonSerializer.Deserialize<LspLocation>(e.GetRawText(), ReadOptions);
                if (loc?.Uri is not null && loc.Range is not null) into.Add(loc);
            }
        }
    }

    /// <summary>Splits a command line on whitespace, honouring double quotes — enough for <c>"C:\path with space\x.exe" --stdio</c>.</summary>
    internal static (string File, List<string> Arguments) SplitCommandLine(string commandLine)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var c in commandLine)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        if (tokens.Count == 0) throw new ArgumentException("--lsp needs a command line.", nameof(commandLine));
        return (tokens[0], tokens.Skip(1).ToList());
    }
}

/// <summary>One request after readiness: what, on which file, how long, and whether it was the file's first request.</summary>
internal sealed record LspTiming(string Method, string File, double Milliseconds, bool FirstForFile);

// ---- LSP 3.17 DTOs (only what the diff reads) -------------------------------------------------------------------

internal sealed record LspPosition
{
    [JsonPropertyName("line")] public int Line { get; init; }
    [JsonPropertyName("character")] public int Character { get; init; }
}

internal sealed record LspRange
{
    [JsonPropertyName("start")] public LspPosition Start { get; init; } = new();
    [JsonPropertyName("end")] public LspPosition End { get; init; } = new();
}

internal sealed record LspLocation
{
    [JsonPropertyName("uri")] public string Uri { get; init; } = string.Empty;
    [JsonPropertyName("range")] public LspRange Range { get; init; } = new();

    /// <summary>Call sites inside the caller (<c>incomingCalls.fromRanges</c>); empty for plain locations.</summary>
    [JsonIgnore] public IReadOnlyList<LspRange> Sites { get; init; } = [];
}

internal sealed record LspLocationLink
{
    [JsonPropertyName("targetUri")] public string? TargetUri { get; init; }
    [JsonPropertyName("targetRange")] public LspRange? TargetRange { get; init; }
    [JsonPropertyName("targetSelectionRange")] public LspRange? TargetSelectionRange { get; init; }
}

internal sealed record DocumentSymbol
{
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("detail")] public string? Detail { get; init; }
    [JsonPropertyName("kind")] public int Kind { get; init; }
    [JsonPropertyName("range")] public LspRange Range { get; init; } = new();
    [JsonPropertyName("selectionRange")] public LspRange SelectionRange { get; init; } = new();
    [JsonPropertyName("children")] public List<DocumentSymbol>? Children { get; init; }
}

internal sealed record CallHierarchyItem
{
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("kind")] public int Kind { get; init; }
    [JsonPropertyName("uri")] public string? Uri { get; init; }
    [JsonPropertyName("range")] public LspRange? Range { get; init; }
    [JsonPropertyName("selectionRange")] public LspRange? SelectionRange { get; init; }
}
