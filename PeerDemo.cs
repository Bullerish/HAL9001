namespace HAL9001;

/// <summary>
/// Step 2 demo: stand up a <see cref="PeerNode"/> in host or join role, exchange an
/// automatic greeting to prove the link works, then let the user chat with the peer.
/// </summary>
public static class PeerDemo
{
    /// <summary>Run as the HOST: listen for a peer, then chat.</summary>
    public static async Task RunHostAsync(int port)
    {
        await using var peer = new PeerNode();
        WireUp(peer);

        await peer.ListenAndAcceptAsync(port);

        // Auto-greeting: guarantees there's traffic in BOTH directions the moment the
        // link is up, so you can see it working without typing anything.
        await peer.SendAsync($"Hello from the HOST instance (port {port}).");

        await RunChatAsync(peer);
    }

    /// <summary>Run as the JOINER: connect to a peer, then chat.</summary>
    public static async Task RunJoinAsync(string host, int port)
    {
        await using var peer = new PeerNode();
        WireUp(peer);

        await peer.ConnectAsync(host, port);
        await peer.SendAsync("Hello from the JOINER instance.");

        await RunChatAsync(peer);
    }

    /// <summary>Print incoming messages and link state. Events fire on a background thread.</summary>
    private static void WireUp(PeerNode peer)
    {
        peer.MessageReceived += message => Console.WriteLine($"[received] {message}");
        peer.PeerDisconnected += () => Console.WriteLine("[peer] disconnected.");
    }

    /// <summary>
    /// Read console lines and send each to the peer. Works interactively (you type) and
    /// non-interactively (piped input): when input ends, we wait a brief grace period so
    /// any in-flight messages can arrive before we tear the connection down.
    /// </summary>
    private static async Task RunChatAsync(PeerNode peer)
    {
        Console.WriteLine("Type a message and press Enter to send it to the peer.");
        Console.WriteLine("Type 'exit' (or end input) to quit.");
        Console.WriteLine();

        while (true)
        {
            string? line = Console.ReadLine();

            if (line is null || line.Equals("exit", StringComparison.OrdinalIgnoreCase))
                break;
            if (line.Length == 0)
                continue;

            await peer.SendAsync(line);
        }

        // Grace period: let the background receive loop deliver any last messages
        // (e.g. the peer's greeting) before DisposeAsync closes the socket.
        await Task.Delay(TimeSpan.FromSeconds(1));
    }
}
