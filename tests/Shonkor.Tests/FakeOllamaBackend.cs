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
    private readonly HttpStatusCode _status;
    private int _requests;

    /// <summary>Base URL, e.g. <c>http://127.0.0.1:51234</c>. No trailing slash — the services append paths.</summary>
    public string Url { get; }

    /// <summary>How many requests reached the backend. This is the attempt count.</summary>
    public int Requests => Volatile.Read(ref _requests);

    /// <param name="status">
    /// The status every request gets. <see cref="HttpStatusCode.ServiceUnavailable"/> (503) is <i>transient</i>
    /// by <c>OllamaRetry</c>'s rules, so the background pipeline retries it and the blocking one must not —
    /// which is what makes the request count a readable signal for telling the two policies apart.
    /// </param>
    public FakeOllamaBackend(HttpStatusCode status = HttpStatusCode.ServiceUnavailable)
    {
        _status = status;
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
            try
            {
                ctx.Response.StatusCode = (int)_status;
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

    public void Dispose() => _listener.Close();
}
