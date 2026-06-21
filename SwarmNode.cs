using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace HAL9001;

/// <summary>
/// The N-peer swarm transport, now churn-survivable (rung 2). It keeps a *collection* of
/// peers and actively maintains the full mesh as nodes drop, restart, and join late.
///
/// MEMBERSHIP (_known) vs CONNECTIONS (_peers):
///   _known = endpoints we believe are swarm members and should be connected to (seeded from
///            the CLI roster, grown by gossip). _peers = the live connections we currently
///            hold. A background MAINTENANCE LOOP continuously tries to make _peers match the
///            part of _known it's responsible for dialing.
///
/// DIAL DIRECTION (how we avoid duplicate connections):
///   For each pair, only the LOWER-port node dials; the higher only accepts. So two nodes
///   never dial each other at the same time — a simultaneous mutual reconnect can't happen by
///   construction. As a safety net for stale-link races, _peers is keyed by identity and a
///   second connection for an identity atomically REPLACES the stale one (match-on-remove so
///   the dead link's cleanup can't evict the fresh one). Net result: exactly one live
///   connection per pair, always.
///
/// REJOIN / LATE JOIN:
///   The maintenance loop never gives up early (retries every couple seconds, up to a cap),
///   so a peer that dropped or started late is reconnected whenever it comes back. A newcomer
///   learns members beyond its own CLI roster via gossip: on connect, peers exchange their
///   known sets, so the mesh closes even if a node was only told about one bootstrap peer.
///
/// CLEAN EXIT vs UNEXPECTED DROP:
///   On a deliberate exit the node BROADCASTS a Goodbye first; receivers remove it from
///   _known and never redial it. A process that's killed sends no Goodbye, so its read loop
///   just hits EOF — that's an unexpected drop, the peer stays in _known, and we reconnect.
///   The presence/absence of a Goodbye is exactly how the two are told apart.
///
/// Still transport only — no coordinator/election (those are later rungs).
/// </summary>
public sealed class SwarmNode : IAsyncDisposable
{
    private const int RetryIntervalMs = 2000;   // maintenance loop cadence
    private const int MaxDialAttempts = 60;     // ~2 min of retries, then presume that peer gone (cap)

    private readonly int _listenPort;
    private readonly string _myId;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;

    private readonly ConcurrentDictionary<string, PeerLink> _peers = new();   // live connections, by identity
    private readonly ConcurrentDictionary<string, byte> _known = new();       // members to stay connected to (excl self)
    private readonly ConcurrentDictionary<string, int> _dialFailures = new(); // consecutive dial failures per endpoint

    public event Action<string, PeerMessage>? MessageReceived;

    public SwarmNode(int listenPort)
    {
        _listenPort = listenPort;
        _myId = $"127.0.0.1:{listenPort}";
    }

    public string Id => _myId;
    public IReadOnlyList<string> Peers => _peers.Keys.OrderBy(k => k).ToList();

    public Task StartAsync(IReadOnlyList<int> peerPorts)
    {
        foreach (int port in peerPorts.Where(p => p != _listenPort))
            _known.TryAdd($"127.0.0.1:{port}", 0);

        _listener = new TcpListener(IPAddress.Loopback, _listenPort);
        _listener.Start();
        Console.WriteLine($"[swarm] {_myId} listening — maintaining mesh with: [{string.Join(", ", _known.Keys.OrderBy(k => k))}]");

        _ = AcceptLoopAsync();
        _ = MaintenanceLoopAsync();
        return Task.CompletedTask;
    }

    // Keep accepting inbound connections forever (newcomers, rejoiners, the lower-port side of each pair).
    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(_cts.Token); }
            catch { break; }
            _ = HandleConnectionAsync(client, knownId: null); // identity arrives via Hello
        }
    }

    // Continuously make the mesh whole: dial known higher-port peers we're not connected to.
    private async Task MaintenanceLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            foreach (string id in _known.Keys)
            {
                if (_peers.ContainsKey(id)) { _dialFailures[id] = 0; continue; }   // already connected
                if (PortOf(id) <= _listenPort) continue;                            // dial-direction: lower dials higher
                if (_dialFailures.GetValueOrDefault(id) >= MaxDialAttempts) continue; // gave up (cap)
                _ = DialOnceAsync(id);
            }
            try { await Task.Delay(RetryIntervalMs, _cts.Token); } catch { break; }
        }
    }

    private async Task DialOnceAsync(string id)
    {
        if (_peers.ContainsKey(id)) return;
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, PortOf(id), _cts.Token);
            _dialFailures[id] = 0;
            await HandleConnectionAsync(client, knownId: id); // we know whom we dialed
        }
        catch (OperationCanceledException) { client.Dispose(); }
        catch
        {
            client.Dispose();
            _dialFailures.AddOrUpdate(id, 1, (_, v) => v + 1); // count toward the cap
        }
    }

    // One connection's whole life: register, exchange identity + membership, read until it drops.
    private async Task HandleConnectionAsync(TcpClient client, string? knownId)
    {
        NetworkStream stream = client.GetStream();
        string? id = knownId;
        PeerLink? link = null;

        try
        {
            if (id is not null)
            {
                link = Register(id, client, stream);
                await SendOnLinkAsync(link, PeerMessageKind.Hello, _myId);          // announce who we are
                await SendOnLinkAsync(link, PeerMessageKind.Membership, RosterText()); // gossip what we know
            }

            while (!_cts.IsCancellationRequested)
            {
                PeerMessage? message = await ReadFrameAsync(stream, _cts.Token);
                if (message is null) break; // EOF — peer closed (clean exit already removed it from _known, or it's a drop)

                switch (message.Kind)
                {
                    case PeerMessageKind.Hello:
                        if (id is null)
                        {
                            id = message.Text;
                            link = Register(id, client, stream);
                            await SendOnLinkAsync(link, PeerMessageKind.Membership, RosterText());
                        }
                        break;

                    case PeerMessageKind.Membership:
                        MergeRoster(message.Text);
                        break;

                    case PeerMessageKind.Goodbye:
                        // Peer is leaving on purpose → forget it so we don't try to reconnect.
                        // (An unexpected drop sends no Goodbye, so it stays in _known and the
                        // maintenance loop reconnects — that's how the two cases differ.)
                        string leaver = message.Text.Length > 0 ? message.Text : (id ?? "");
                        _known.TryRemove(leaver, out _);
                        break;

                    default:
                        if (id is not null) MessageReceived?.Invoke(id, message);
                        break;
                }
            }
        }
        catch { /* link dropped */ }
        finally
        {
            // Drop the live link. On an UNEXPECTED drop the peer stays in _known, so the
            // maintenance loop reconnects; a CLEAN exit already removed it from _known via its
            // Goodbye, so it stays gone.
            if (link is not null) RemovePeer(id!, link);
            else client.Dispose();
        }
    }

    // ── Connection bookkeeping ───────────────────────────────────────────────────────────
    // Add a peer; if one already exists for this identity (stale-link race), the NEW connection
    // atomically replaces it so we always keep the freshest link — one per pair.
    private PeerLink Register(string id, TcpClient client, NetworkStream stream)
    {
        var link = new PeerLink(id, client, stream);
        while (true)
        {
            if (_peers.TryAdd(id, link))
            {
                Console.WriteLine($"[swarm] + {id} connected");
                PrintPeers();
                return link;
            }
            if (_peers.TryGetValue(id, out PeerLink? existing) && _peers.TryUpdate(id, link, existing))
            {
                existing.Dispose(); // replaced a stale link; peer list unchanged, no reprint
                return link;
            }
        }
    }

    // Remove only if this exact link is still the registered one (so a dead link's cleanup
    // can't evict a fresh replacement).
    private void RemovePeer(string id, PeerLink link)
    {
        if (_peers.TryRemove(new KeyValuePair<string, PeerLink>(id, link)))
        {
            link.Dispose();
            Console.WriteLine($"[swarm] - {id} disconnected");
            PrintPeers();
        }
    }

    public void PrintPeers() =>
        Console.WriteLine($"[swarm] {_myId} peers ({_peers.Count}): [{string.Join(", ", Peers)}]");

    // ── Membership gossip ────────────────────────────────────────────────────────────────
    private string RosterText() =>
        string.Join(",", _known.Keys.Append(_myId).Distinct().OrderBy(k => k));

    private void MergeRoster(string text)
    {
        bool grew = false;
        foreach (string raw in text.Split(','))
        {
            string ep = raw.Trim();
            if (ep.Length == 0 || ep == _myId) continue;
            if (_known.TryAdd(ep, 0)) grew = true; // a member we hadn't heard of → maintenance will dial it
        }
        // Only re-broadcast when we actually learned something, so gossip converges and stops.
        if (grew) _ = BroadcastAsync(PeerMessageKind.Membership, RosterText());
    }

    // ── Sending ──────────────────────────────────────────────────────────────────────────
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
            catch { /* one bad link must not stop the broadcast */ }
        }
    }

    private static async Task SendOnLinkAsync(PeerLink link, PeerMessageKind kind, string text)
    {
        await link.SendLock.WaitAsync();
        try { await WriteFrameAsync(link.Stream, kind, text, CancellationToken.None); }
        finally { link.SendLock.Release(); }
    }

    // ── Framing: [4-byte length][1-byte kind][UTF-8 text] ────────────────────────────────
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

    private static int PortOf(string id)
    {
        int colon = id.LastIndexOf(':');
        return colon >= 0 && int.TryParse(id[(colon + 1)..], out int p) ? p : -1;
    }

    public async ValueTask DisposeAsync()
    {
        // Clean exit: tell everyone we're leaving so they don't try to reconnect to us.
        try { await BroadcastAsync(PeerMessageKind.Goodbye, _myId); } catch { }

        _cts.Cancel();
        _listener?.Stop();
        foreach (PeerLink link in _peers.Values.ToArray()) link.Dispose();
        _peers.Clear();
        _cts.Dispose();
    }

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
