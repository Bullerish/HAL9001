using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace HAL9001;

/// <summary>
/// One side of a TCP peer-to-peer link.
///
/// Both program instances run the SAME code; the only difference is whether an instance
/// LISTENS for a peer (host role) or DIALS one (join role). That mirrors the end goal:
/// two identical agents talking to each other. Once the single TCP connection is up it
/// is fully bidirectional — either side can <see cref="SendAsync"/>, and both raise
/// <see cref="MessageReceived"/> when a message arrives.
///
/// This class deliberately knows nothing about handlers or questions — it just moves
/// strings across the wire reliably. Wiring it to the registry comes in a later step.
/// </summary>
public sealed class PeerNode : IAsyncDisposable
{
    // The connected socket and the byte stream over it. Null until host/join completes.
    private TcpClient? _connection;
    private NetworkStream? _stream;

    // One token controls shutdown of the whole node (the receive loop watches it).
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Raised (on a background thread) for each complete message from the peer.</summary>
    public event Action<PeerMessage>? MessageReceived;

    /// <summary>Raised once when the peer disconnects or the link drops.</summary>
    public event Action? PeerDisconnected;

    /// <summary>
    /// HOST role: bind <paramref name="port"/> and wait for exactly one peer to connect.
    /// </summary>
    public async Task ListenAndAcceptAsync(int port)
    {
        // TcpListener is the "server" socket: it binds an address+port and accepts clients.
        // We bind to loopback (127.0.0.1) so this stays local for now.
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Console.WriteLine($"[peer] Listening on 127.0.0.1:{port} — waiting for a peer to connect...");

        // AcceptTcpClientAsync blocks (asynchronously) until someone connects, then hands
        // back a TcpClient representing that connection.
        _connection = await listener.AcceptTcpClientAsync(_cts.Token);
        listener.Stop(); // one peer is enough for now; stop accepting further connections.

        _stream = _connection.GetStream();
        Console.WriteLine($"[peer] Peer connected from {_connection.Client.RemoteEndPoint}.");
        BeginReceiveLoop();
    }

    /// <summary>
    /// JOIN role: dial <paramref name="host"/>:<paramref name="port"/>. Retries briefly so
    /// it doesn't matter which instance you start first.
    /// </summary>
    public async Task ConnectAsync(string host, int port)
    {
        Console.WriteLine($"[peer] Connecting to {host}:{port}...");

        // The host may not be listening yet, so retry a handful of times (~5s total).
        // A TcpClient whose connect attempt failed can't be reused, so we make a fresh
        // one each attempt.
        for (int attempt = 1; ; attempt++)
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(host, port, _cts.Token);
                _connection = client;
                break;
            }
            catch (SocketException) when (attempt < 25)
            {
                client.Dispose();
                await Task.Delay(200, _cts.Token);
            }
        }

        _stream = _connection!.GetStream();
        Console.WriteLine($"[peer] Connected to {host}:{port}.");
        BeginReceiveLoop();
    }

    /// <summary>
    /// Start the background loop that reads framed messages until the link closes.
    /// Fire-and-forget: it runs independently of whatever the main thread is doing
    /// (e.g. waiting on console input), which is what makes the chat feel bidirectional.
    /// </summary>
    private void BeginReceiveLoop()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    PeerMessage? message = await ReadMessageAsync(_stream!, _cts.Token);
                    if (message is null) break;        // peer closed the connection cleanly
                    MessageReceived?.Invoke(message);
                }
            }
            catch (OperationCanceledException) { /* normal: we're shutting down */ }
            catch (IOException)                { /* link dropped; treat as disconnect */ }
            finally
            {
                PeerDisconnected?.Invoke();
            }
        });
    }

    /// <summary>Send a typed message (Chat / Question / …) to the peer.</summary>
    public Task SendAsync(PeerMessageKind kind, string text)
    {
        // No-op if we're not connected, so callers can "send to the peer" unconditionally
        // and it just does nothing when there's no peer.
        if (_stream is null) return Task.CompletedTask;
        return WriteMessageAsync(_stream, kind, text, _cts.Token);
    }

    /// <summary>Convenience: send a plain chat line (what the Step 2 demo uses).</summary>
    public Task SendAsync(string text) => SendAsync(PeerMessageKind.Chat, text);

    // =====================================================================================
    // MESSAGE FRAMING — the key networking lesson of this step.
    //
    // TCP is a STREAM of bytes, not a sequence of messages. The runtime is free to:
    //   * split one SendAsync across several reads on the other side, OR
    //   * coalesce several SendAsyncs into a single read.
    // So "one read == one message" is NOT guaranteed and you must impose your own message
    // boundaries. We use length-prefix framing. As of Step 6A each frame also carries a
    // one-byte KIND tag so the receiver knows what sort of message it is:
    //
    //        [ 4-byte big-endian length ][ 1-byte kind ][ UTF-8 text bytes ]
    //                                     └──────────── payload (length bytes) ───────────┘
    //
    // The reader reads exactly 4 bytes for the length, then exactly that many payload bytes;
    // the first payload byte is the kind, the rest is the UTF-8 text.
    // =====================================================================================

    private static async Task WriteMessageAsync(NetworkStream stream, PeerMessageKind kind, string text, CancellationToken ct)
    {
        byte[] textBytes = Encoding.UTF8.GetBytes(text);

        // payload = [kind byte][text bytes]
        byte[] payload = new byte[1 + textBytes.Length];
        payload[0] = (byte)kind;
        textBytes.CopyTo(payload, 1);

        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);

        await stream.WriteAsync(header, ct);   // 1) how many payload bytes follow
        await stream.WriteAsync(payload, ct);  // 2) kind + text
        await stream.FlushAsync(ct);
    }

    private static async Task<PeerMessage?> ReadMessageAsync(NetworkStream stream, CancellationToken ct)
    {
        byte[] header = new byte[4];
        try
        {
            // ReadExactlyAsync (net7.0+) loops internally until the buffer is completely
            // filled. If the stream ends before that, it throws EndOfStreamException — which
            // for us means the peer hung up between messages, i.e. a clean disconnect.
            await stream.ReadExactlyAsync(header, ct);
        }
        catch (EndOfStreamException)
        {
            return null;
        }

        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct); // read exactly the advertised length

        if (length < 1) return new PeerMessage(PeerMessageKind.Chat, string.Empty); // malformed guard

        var kind = (PeerMessageKind)payload[0];
        string text = Encoding.UTF8.GetString(payload, 1, payload.Length - 1);
        return new PeerMessage(kind, text);
    }

    /// <summary>Cancel the receive loop and release the socket.</summary>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_stream is not null) await _stream.DisposeAsync();
        _connection?.Dispose();
        _cts.Dispose();
    }
}
