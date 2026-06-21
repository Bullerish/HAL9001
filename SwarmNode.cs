using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace HAL9001;

/// <summary>
/// The N-peer transport — rung one of the swarm. Where <see cref="PeerNode"/> holds exactly
/// one connection, a SwarmNode keeps a *collection* of peers: it never stops accepting inbound
/// connections AND dials out to others, holding all of them at once.
///
/// TOPOLOGY (full mesh via explicit addresses + a "dial-higher" rule):
///   Each instance is launched with its own listen port and the ports of the other nodes.
///   It listens, and it dials only the peers whose port is GREATER than its own. Every node
///   accepts inbound from the lower-numbered ones. That single rule gives exactly one
///   connection per pair — a full mesh with no duplicates — without any coordinator.
///   e.g. 5001 dials 5002,5003 · 5002 dials 5003 · 5003 dials nobody → all three meshed.
///
/// IDENTITY: a peer is identified by its listen endpoint ("127.0.0.1:PORT"), which is stable
///   across the session. The dialer already knows whom it dialed; the ACCEPTOR can't tell
///   (the inbound socket's source port is ephemeral), so the dialer's first frame is a Hello
///   carrying its listen identity. Peers are tracked in a dictionary keyed by that identity.
///
/// This is transport only: it can send to one peer or broadcast to all, but makes NO swarm
/// decisions — coordinator/election/routing are later rungs.
/// </summary>
public sealed class SwarmNode : IAsyncDisposable
{
    private readonly int _listenPort;
    private readonly string _myId;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;

    // The peer collection, keyed by identity. ConcurrentDictionary because connections are
    // added/removed from multiple background threads (the accept loop + each receive loop).
    private readonly ConcurrentDictionary<string, PeerLink> _peers = new();

    /// <summary>Raised (on a background thread) when a peer message arrives: who it's from + the message.</summary>
    public event Action<string, PeerMessage>? MessageReceived;

    public SwarmNode(int listenPort)
    {
        _listenPort = listenPort;
        _myId = $"127.0.0.1:{listenPort}";
    }

    public string Id => _myId;
    public IReadOnlyList<string> Peers => _peers.Keys.OrderBy(k => k).ToList();

    /// <summary>Start listening and dial the higher-numbered peers (so each pair connects once).</summary>
    public Task StartAsync(IReadOnlyList<int> peerPorts)
    {
        _listener = new TcpListener(IPAddress.Loopback, _listenPort);
        _listener.Start();
        Console.WriteLine($"[swarm] {_myId} listening — accepting peers...");
        _ = AcceptLoopAsync();

        foreach (int port in peerPorts.Where(p => p != _listenPort && p > _listenPort))
            _ = DialAsync(port);

        return Task.CompletedTask;
    }

    // Keep accepting inbound connections for the whole session (not just the first).
    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(_cts.Token); }
            catch { break; } // listener stopped / cancelled
            _ = HandleConnectionAsync(client, knownId: null); // identity arrives via Hello
        }
    }

    // Dial one peer, retrying until it's up (so launch order doesn't matter).
    private async Task DialAsync(int port)
    {
        string id = $"127.0.0.1:{port}";
        for (int attempt = 1; ; attempt++)
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, _cts.Token);
                await HandleConnectionAsync(client, knownId: id); // we know who we dialed
                return;
            }
            catch (OperationCanceledException) { client.Dispose(); return; }
            catch (Exception) when (attempt < 25)
            {
                client.Dispose();
                try { await Task.Delay(200, _cts.Token); } catch { return; }
            }
            catch { client.Dispose(); return; }
        }
    }

    // One connection's whole life: register, (if dialer) announce identity, then read until it drops.
    private async Task HandleConnectionAsync(TcpClient client, string? knownId)
    {
        NetworkStream stream = client.GetStream();
        string? id = knownId;
        PeerLink? link = null;

        try
        {
            if (id is not null)
            {
                // Outbound: we know the peer. Register, then announce ourselves with Hello.
                link = AddPeer(id, client, stream);
                if (link is null) { client.Dispose(); return; } // already connected → drop dup
                await SendOnLinkAsync(link, PeerMessageKind.Hello, _myId);
            }

            while (!_cts.IsCancellationRequested)
            {
                PeerMessage? message = await ReadFrameAsync(stream, _cts.Token);
                if (message is null) break; // peer closed

                if (message.Kind == PeerMessageKind.Hello)
                {
                    // Inbound connection telling us who it is.
                    if (id is null)
                    {
                        id = message.Text;
                        link = AddPeer(id, client, stream);
                        if (link is null) break; // dup → close this connection
                    }
                    continue;
                }

                if (id is not null) MessageReceived?.Invoke(id, message);
            }
        }
        catch { /* link dropped */ }
        finally
        {
            if (id is not null) RemovePeer(id);
            else client.Dispose();
        }
    }

    // ── Peer collection management (the heart of join/leave) ─────────────────────────────
    private PeerLink? AddPeer(string id, TcpClient client, NetworkStream stream)
    {
        var link = new PeerLink(id, client, stream);
        if (_peers.TryAdd(id, link))
        {
            Console.WriteLine($"[swarm] + {id} connected");
            PrintPeers();
            return link;
        }
        link.Dispose();      // someone beat us to it (shouldn't happen with dial-higher) — drop
        return null;
    }

    private void RemovePeer(string id)
    {
        if (_peers.TryRemove(id, out PeerLink? link))
        {
            link.Dispose();
            Console.WriteLine($"[swarm] - {id} disconnected");
            PrintPeers();
        }
    }

    public void PrintPeers() =>
        Console.WriteLine($"[swarm] {_myId} peers ({_peers.Count}): [{string.Join(", ", Peers)}]");

    // ── Send (the transport capability the swarm will use) ───────────────────────────────
    public async Task SendToAsync(string id, PeerMessageKind kind, string text)
    {
        if (_peers.TryGetValue(id, out PeerLink? link))
        {
            try { await SendOnLinkAsync(link, kind, text); }
            catch (Exception ex) { Console.WriteLine($"[swarm] send to {id} failed: {ex.Message}"); }
        }
        else Console.WriteLine($"[swarm] no peer {id}");
    }

    public async Task BroadcastAsync(PeerMessageKind kind, string text)
    {
        foreach (PeerLink link in _peers.Values.ToArray())
        {
            try { await SendOnLinkAsync(link, kind, text); }
            catch { /* a single bad link must not stop the broadcast */ }
        }
    }

    // Serialize writes per link — two broadcasts/sends could otherwise interleave bytes on one socket.
    private static async Task SendOnLinkAsync(PeerLink link, PeerMessageKind kind, string text)
    {
        await link.SendLock.WaitAsync();
        try { await WriteFrameAsync(link.Stream, kind, text, CancellationToken.None); }
        finally { link.SendLock.Release(); }
    }

    // ── Framing: [4-byte length][1-byte kind][UTF-8 text] (same scheme as PeerNode) ──────
    private static async Task WriteFrameAsync(NetworkStream stream, PeerMessageKind kind, string text, CancellationToken ct)
    {
        byte[] textBytes = Encoding.UTF8.GetBytes(text);
        byte[] payload = new byte[1 + textBytes.Length];
        payload[0] = (byte)kind;
        textBytes.CopyTo(payload, 1);

        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<PeerMessage?> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        byte[] header = new byte[4];
        try { await stream.ReadExactlyAsync(header, ct); }
        catch (EndOfStreamException) { return null; }

        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 1) return new PeerMessage(PeerMessageKind.Chat, string.Empty);

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct);
        return new PeerMessage((PeerMessageKind)payload[0], Encoding.UTF8.GetString(payload, 1, length - 1));
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener?.Stop();
        foreach (PeerLink link in _peers.Values.ToArray()) link.Dispose();
        _peers.Clear();
        _cts.Dispose();
        await Task.CompletedTask;
    }

    // One live peer connection.
    private sealed class PeerLink : IDisposable
    {
        public string Id { get; }
        public TcpClient Client { get; }
        public NetworkStream Stream { get; }
        public SemaphoreSlim SendLock { get; } = new(1, 1);

        public PeerLink(string id, TcpClient client, NetworkStream stream)
        {
            Id = id; Client = client; Stream = stream;
        }

        public void Dispose()
        {
            try { Stream.Dispose(); } catch { }
            try { Client.Dispose(); } catch { }
            SendLock.Dispose();
        }
    }
}
