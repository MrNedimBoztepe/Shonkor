// Licensed to Shonkor under the MIT License.

using System.Net;
using System.Net.Sockets;

namespace Shonkor.Tests;

/// <summary>
/// A backend that fails at the <b>transport</b> level, in the ways a real one does — driven over a real socket
/// so the exceptions the classifier sees are the exceptions .NET actually produces (#218).
///
/// <para>
/// This exists because inventing the exception was how the last two bugs hid. #213 asserted "the RAG path never
/// retries a timeout" against a hand-built <c>TaskCanceledException</c>; the real one carries a
/// <c>SocketException</c> at the bottom, the classifier keyed on that, and the rule was broken in production
/// while the test stayed green (#215). A fixture that is easier than reality is not a fixture.
/// </para>
/// </summary>
internal sealed class MisbehavingBackend : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Mode _mode;
    private int _requests;

    internal enum Mode
    {
        /// <summary>Accept the connection, then reset it before sending anything. Nothing was ever answered.</summary>
        ResetBeforeResponding,

        /// <summary>
        /// Answer <c>200 OK</c>, promise a body, send part of it, then drop the connection. The backend
        /// <b>responded</b> and the model was already generating — this is the expensive failure the blocking
        /// path must not re-run, and the one it used to mistake for a connection error.
        /// </summary>
        DieMidBody
    }

    public string Url { get; }
    public int Requests => Volatile.Read(ref _requests);

    public MisbehavingBackend(Mode mode)
    {
        _mode = mode;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _ = Task.Run(ServeAsync);
    }

    private async Task ServeAsync()
    {
        while (true)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false); }
            catch { return; } // listener stopped

            Interlocked.Increment(ref _requests);

            try
            {
                if (_mode == Mode.DieMidBody)
                {
                    var stream = client.GetStream();
                    // Content-Length promises 100 bytes; we send 5 and hang up. A truncated generation.
                    await stream.WriteAsync(
                        "HTTP/1.1 200 OK\r\nContent-Length: 100\r\nContent-Type: application/json\r\n\r\n"u8.ToArray())
                        .ConfigureAwait(false);
                    await stream.WriteAsync("{\"a\":"u8.ToArray()).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }

                client.LingerState = new LingerOption(true, 0); // RST, not a graceful close
                client.Close();
            }
            catch { /* the client may already be gone */ }
        }
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch { /* already stopped */ }
    }
}
