// Licensed to Shonkor under the MIT License.

using System.Net;
using System.Net.Sockets;

namespace Shonkor.Tests;

/// <summary>
/// A stand-in Ollama that fails in a chosen way and <b>counts the requests that actually reached it</b>.
///
/// <para>
/// The attempt count is how the resilience policies are observed from the outside (#179, #213): the policy is
/// applied at the call site, so the only honest way to ask "how many times did we retry?" is to ask the
/// backend how many times we knocked. It also means the guards assert real HTTP behaviour rather than the
/// shape of a DI graph, and so survive a policy attached by a route the test did not anticipate.
/// </para>
/// </summary>
internal sealed class FakeOllamaBackend : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly HttpStatusCode? _status;
    private readonly string? _body;
    private readonly string? _stallLine;
    private readonly IReadOnlyList<string>? _dripLines;
    private readonly TimeSpan _dripGap;
    private readonly List<HttpListenerContext> _hung = [];
    private int _requests;

    /// <summary>
    /// A backend that streams headers + one NDJSON token, flushes it, then <b>holds the connection open with
    /// no further bytes</b> — the shape of a model that emits a little then wedges (#230). Headers arrive (so
    /// <c>ResponseHeadersRead</c> returns and the first token is delivered), but the next body read blocks
    /// forever, which is exactly what the stream idle-timeout must catch.
    /// </summary>
    public static FakeOllamaBackend ThatStreamsThenStalls(string firstLine) =>
        new(HttpStatusCode.OK, stallLine: firstLine);

    /// <summary>
    /// A backend that streams <paramref name="lines"/> one at a time with <paramref name="gap"/> between them,
    /// then closes. A slow-but-alive model: if each gap is under the idle timeout, the guard must NOT trip
    /// (it resets on every token), even if the total exceeds the idle window (#230).
    /// </summary>
    public static FakeOllamaBackend ThatDripsThenCompletes(IReadOnlyList<string> lines, TimeSpan gap) =>
        new(HttpStatusCode.OK, dripLines: lines, dripGap: gap);

    /// <summary>
    /// A backend that <b>accepts the connection and then never answers</b> — the only way to drive a real
    /// <see cref="HttpClient"/> timeout, as opposed to a stand-in exception. Held contexts are never closed,
    /// so the client waits until its own timeout elapses (#215).
    /// </summary>
    public static FakeOllamaBackend ThatNeverResponds() => new(status: null);

    /// <summary>Base URL, e.g. <c>http://127.0.0.1:51234</c>. No trailing slash — the services append paths.</summary>
    public string Url { get; }

    /// <summary>How many requests reached the backend. This is the attempt count.</summary>
    public int Requests => Volatile.Read(ref _requests);

    /// <summary>
    /// A backend that <b>answers <c>200 OK</c> with an unusable payload</b> — the deterministic failure. It
    /// will answer identically on every attempt, so retrying it is pure waste against a backend that cannot
    /// succeed. Pass an empty JSON object, a body missing the expected field, or malformed JSON (#222).
    /// </summary>
    public static FakeOllamaBackend ThatAnswers(string body) => new(HttpStatusCode.OK, body);

    /// <param name="status">
    /// The status every request gets. <see cref="HttpStatusCode.ServiceUnavailable"/> (503) is <i>transient</i>
    /// by <c>OllamaRetry</c>'s rules, so the background pipeline retries it and the blocking one must not —
    /// which is what makes the request count a readable signal for telling the two policies apart.
    /// </param>
    /// <param name="body">Response body to return with <paramref name="status"/>, if any.</param>
    public FakeOllamaBackend(HttpStatusCode? status = HttpStatusCode.ServiceUnavailable, string? body = null,
        string? stallLine = null, IReadOnlyList<string>? dripLines = null, TimeSpan dripGap = default)
    {
        _status = status;
        _body = body;
        _stallLine = stallLine;
        _dripLines = dripLines;
        _dripGap = dripGap;
        Url = $"http://127.0.0.1:{FreePort()}";
        _listener.Prefixes.Add($"{Url}/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch { return; } // listener closed
            Interlocked.Increment(ref _requests);

            if (_status is null)
            {
                // Accept and hold. Never respond, never close — the client must hit its own timeout.
                lock (_hung) _hung.Add(ctx);
                continue;
            }

            if (_stallLine is not null)
            {
                // Headers + one token, flushed, then hold open forever (no more tokens, no done, no close).
                try
                {
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/x-ndjson";
                    ctx.Response.SendChunked = true;
                    var bytes = System.Text.Encoding.UTF8.GetBytes(_stallLine);
                    await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                    await ctx.Response.OutputStream.FlushAsync().ConfigureAwait(false);
                }
                catch { /* client already gone */ }
                lock (_hung) _hung.Add(ctx); // hold; the idle-timeout guard on the client must end this
                continue;
            }

            if (_dripLines is not null)
            {
                // Stream each line with a gap, then close cleanly. A slow-but-alive backend.
                try
                {
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/x-ndjson";
                    ctx.Response.SendChunked = true;
                    for (var i = 0; i < _dripLines.Count; i++)
                    {
                        if (i > 0) await Task.Delay(_dripGap).ConfigureAwait(false);
                        var bytes = System.Text.Encoding.UTF8.GetBytes(_dripLines[i]);
                        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                        await ctx.Response.OutputStream.FlushAsync().ConfigureAwait(false);
                    }
                    ctx.Response.Close();
                }
                catch { /* client already gone */ }
                continue;
            }

            try
            {
                ctx.Response.StatusCode = (int)_status.Value;
                if (_body is not null)
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(_body);
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                }
                ctx.Response.Close();
            }
            catch { /* client already gone */ }
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Dispose()
    {
        lock (_hung)
        {
            foreach (var ctx in _hung)
            {
                try { ctx.Response.Abort(); } catch { /* already torn down */ }
            }
            _hung.Clear();
        }
        _listener.Close();
    }
}
