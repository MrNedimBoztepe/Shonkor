// Licensed to Shonkor under the MIT License.

using System.Net;
using System.Net.Sockets;
using System.Text;

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

                    // DRAIN THE REQUEST FIRST. This is not politeness — it is what makes the close a FIN.
                    //
                    // A close() on a socket that still has UNREAD data in its receive buffer makes the TCP
                    // stack send an RST, on every platform, whatever the linger settings and whatever
                    // Shutdown(Send) did earlier. And an RST tells the peer's kernel to DISCARD what is still
                    // in its buffers — including the "200 OK" we just wrote. Which is precisely the bug this
                    // fixture already caused once (#237): on Windows the bytes happened to win the race, so the
                    // test looked like "the backend answered, then died mid-body"; on Linux the reset won, the
                    // client saw NO response at all, and the test was actually exercising "connection reset
                    // before any response" — the OPPOSITE failure.
                    //
                    // The first fix added Shutdown(Send) and a 50 ms delay. That did NOT remove the reset: the
                    // client's POST was still sitting unread, so close() still emitted an RST, and the delay was
                    // only a race window hoping the client drained its receive buffer first. It was a smaller
                    // bug wearing the same clothes. Reading the request out is what actually removes it.
                    await DrainRequestAsync(stream).ConfigureAwait(false);

                    // Content-Length promises 100 bytes; we send 5 and hang up. A truncated generation.
                    await stream.WriteAsync(
                        "HTTP/1.1 200 OK\r\nContent-Length: 100\r\nContent-Type: application/json\r\n\r\n"u8.ToArray())
                        .ConfigureAwait(false);
                    await stream.WriteAsync("{\"a\":"u8.ToArray()).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);

                    // Now the receive buffer is empty, so this is a genuine FIN: the headers and the partial
                    // body are delivered, and the client hits EOF part-way through the promised Content-Length.
                    // A generation that really did start, and really did get cut off.
                    client.Client.Shutdown(SocketShutdown.Send);
                }
                else
                {
                    // ResetBeforeResponding: an RST is exactly right here — nothing was ever answered, and
                    // discarding the (empty) buffer is the point. We deliberately do NOT drain, so the unread
                    // request guarantees the reset even if a platform would otherwise have sent a FIN.
                    client.LingerState = new LingerOption(true, 0);
                }

                client.Close();
            }
            catch { /* the client may already be gone */ }
        }
    }

    /// <summary>
    /// Reads the client's request out of the receive buffer — headers, and any body the <c>Content-Length</c>
    /// announces — so that a subsequent <c>close()</c> is a FIN rather than an RST. See the call site for why
    /// that distinction decides which failure this fixture actually models.
    /// </summary>
    private static async Task DrainRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[8192];
        var seen = new StringBuilder();
        var contentLength = 0;
        var headerEnd = -1;

        // Read until the end of the headers, then read exactly Content-Length more bytes.
        while (headerEnd < 0)
        {
            var n = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (n == 0) return; // client hung up first
            seen.Append(Encoding.ASCII.GetString(buffer, 0, n));
            headerEnd = seen.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);
        }

        var text = seen.ToString();
        foreach (var line in text[..headerEnd].Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line["Content-Length:".Length..].Trim(), out var len))
            {
                contentLength = len;
            }
        }

        var bodyAlready = text.Length - (headerEnd + 4);
        var remaining = contentLength - bodyAlready;
        while (remaining > 0)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining))).ConfigureAwait(false);
            if (n == 0) break;
            remaining -= n;
        }
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch { /* already stopped */ }
    }
}
