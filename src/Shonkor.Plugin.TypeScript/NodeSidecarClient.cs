// Licensed to Shonkor under the MIT License.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shonkor.Plugin.TypeScript;

/// <summary>
/// One structured parse response from the sidecar. Field names match the sidecar's JSON exactly.
/// </summary>
internal sealed class SidecarResponse
{
    public int Id { get; set; }
    public List<SidecarNode> Nodes { get; set; } = new();
    public List<SidecarEdge> Edges { get; set; } = new();
    public List<SidecarDiagnostic> Diagnostics { get; set; } = new();
}

internal sealed class SidecarNode
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? FilePath { get; set; }

    /// <summary>1-based start/end line of the declaration (#293 symbol nodes); null for the module node.</summary>
    public int? StartLine { get; set; }
    public int? EndLine { get; set; }

    public Dictionary<string, string> Properties { get; set; } = new();
}

internal sealed class SidecarEdge
{
    public string SourceId { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public string Relationship { get; set; } = string.Empty;
    public Dictionary<string, string> Properties { get; set; } = new();
}

internal sealed class SidecarDiagnostic
{
    public string Severity { get; set; } = "error";
    public string Message { get; set; } = string.Empty;
    public int? Line { get; set; }
    public int? Code { get; set; }
}

/// <summary>
/// Line-delimited JSON over stdio to a single long-lived Node sidecar process. Modelled on
/// <c>McpProxyClient</c> (which the plugin cannot reference across the ALC boundary, so the pattern is
/// copied): every request carries an <c>id</c> and gets exactly one id-correlated response; a transport
/// failure (the process dying) completes every outstanding request with a synthetic error instead of
/// leaving a caller hung forever. <c>UseShellExecute=false</c>, <c>CreateNoWindow</c>, UTF-8 throughout.
/// One instance drives one process; the adapter recycles by disposing and creating a new instance.
/// </summary>
internal sealed class NodeSidecarClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly ChildProcessGuard? _guard;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<SidecarResponse>> _pending = new();
    private readonly Task _readerTask;
    private int _nextId;
    private volatile bool _disposed;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private NodeSidecarClient(Process process)
    {
        _process = process;
        _guard = ChildProcessGuard.TryCreate(process);
        _readerTask = Task.Run(ReadLoopAsync);
    }

    /// <summary>
    /// Starts the sidecar: <c>node &lt;scriptPath&gt;</c> with redirected UTF-8 stdio. Throws on failure to
    /// start (the adapter catches and degrades).
    /// </summary>
    public static NodeSidecarClient Start(string nodePath, string scriptPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = nodePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? Environment.CurrentDirectory,
        };
        startInfo.ArgumentList.Add(scriptPath);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start node sidecar '{nodePath}'.");
        }

        // Drain stderr so a chatty/erroring sidecar never blocks on a full pipe; it is diagnostic only.
        process.BeginErrorReadLine();
        return new NodeSidecarClient(process);
    }

    /// <summary>Whether the sidecar process is still running.</summary>
    public bool IsAlive
    {
        get
        {
            try { return !_disposed && !_process.HasExited; }
            catch { return false; }
        }
    }

    /// <summary>
    /// Sends one parse request and awaits its id-correlated response, bounded by <paramref name="timeout"/>
    /// via <see cref="Task.WaitAsync(TimeSpan)"/>. A timeout throws <see cref="TimeoutException"/>; a dead
    /// transport throws so the adapter can surface a diagnostic and fall back — never a deadlock.
    /// </summary>
    public Task<SidecarResponse> SendAsync(string filePath, string content, TimeSpan timeout) =>
        SendEnvelopeAsync(id => new { id, filePath, content }, timeout);

    /// <summary>
    /// Sends a #294 whole-program <c>link</c> request (<c>{ id, kind:"link", rootNames, projectDir }</c>) and
    /// awaits its id-correlated response. The sidecar builds ONE program + type-checker over
    /// <paramref name="rootNames"/> and returns cross-file semantic edges. Same timeout/deadlock guarantees as
    /// <see cref="SendAsync"/>.
    /// </summary>
    public Task<SidecarResponse> SendLinkAsync(IReadOnlyCollection<string> rootNames, string projectDir, TimeSpan timeout) =>
        SendEnvelopeAsync(id => new { id, kind = "link", rootNames, projectDir }, timeout);

    /// <summary>
    /// Shared request path: assigns a correlation id, serialises the caller's envelope, writes one line, and
    /// awaits the id-correlated response bounded by <paramref name="timeout"/>. A timeout throws
    /// <see cref="TimeoutException"/>; a dead transport throws — never a deadlock.
    /// </summary>
    private async Task<SidecarResponse> SendEnvelopeAsync(Func<int, object> makeEnvelope, TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsAlive) throw new InvalidOperationException("Node sidecar is not running.");

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<SidecarResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        try
        {
            var line = JsonSerializer.Serialize(makeEnvelope(id), JsonOptions);
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await _process.StandardInput.WriteLineAsync(line).ConfigureAwait(false);
                await _process.StandardInput.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }

            return await tcs.Task.WaitAsync(timeout).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            string? line;
            while ((line = await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                SidecarResponse? response;
                try
                {
                    response = JsonSerializer.Deserialize<SidecarResponse>(line, JsonOptions);
                }
                catch
                {
                    // A malformed line cannot be correlated to an id; skip it (stderr carries sidecar logs).
                    continue;
                }

                if (response != null && _pending.TryRemove(response.Id, out var tcs))
                {
                    tcs.TrySetResult(response);
                }
            }
        }
        catch
        {
            // Reader ended abnormally (process died / pipe broke) — handled uniformly below.
        }
        finally
        {
            FailAllPending("Node sidecar exited before answering.");
        }
    }

    /// <summary>
    /// Completes every outstanding request with a transport failure so no caller hangs on a request id that
    /// will never be answered — the client-side analogue of <c>McpProxyClient</c>'s synthetic error response.
    /// </summary>
    private void FailAllPending(string message)
    {
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var tcs))
            {
                tcs.TrySetException(new IOException(message));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Close stdin first: the sidecar exits cleanly on stdin EOF. Then force-kill anything left.
        try { _process.StandardInput.Close(); } catch { /* ignore */ }

        try
        {
            if (!_process.WaitForExit(2000))
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch { /* already gone */ }

        FailAllPending("Node sidecar disposed.");

        try { await _readerTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { /* ignore */ }

        _guard?.Dispose(); // Belt-and-suspenders OS-level kill.
        _writeLock.Dispose();
        try { _process.Dispose(); } catch { /* ignore */ }
    }
}
