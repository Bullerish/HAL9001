namespace HAL9001;

/// <summary>
/// Rung-1 swarm demo: stand up a <see cref="SwarmNode"/>, mesh with the other instances,
/// and let you watch/exercise the multi-peer transport. NO agent logic, NO coordination —
/// just connectivity plus send-to-one / broadcast, which are what later rungs build on.
/// </summary>
public static class SwarmDemo
{
    public static async Task RunAsync(int myPort, IReadOnlyList<int> peerPorts)
    {
        await using var node = new SwarmNode(myPort);

        // Show incoming messages with their sender so you can see who's talking.
        node.MessageReceived += (from, message) =>
        {
            Console.WriteLine($"\n[from {from}] {message.Kind}: {message.Text}");
            Console.Write("> ");
        };

        await node.StartAsync(peerPorts);

        Console.WriteLine();
        Console.WriteLine($"Swarm node {node.Id}. As peers connect you'll see the peer list update.");
        Console.WriteLine("Commands:  peers           — show my peer list");
        Console.WriteLine("           @<port> <msg>   — send a message to one peer");
        Console.WriteLine("           <msg>           — broadcast to all peers");
        Console.WriteLine("           exit            — quit");
        Console.WriteLine();

        while (true)
        {
            Console.Write("> ");
            string? line = Console.ReadLine();
            if (line is null || line.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase))
                break;

            line = line.Trim();
            if (line.Length == 0) continue;

            if (line.Equals("peers", StringComparison.OrdinalIgnoreCase))
            {
                node.PrintPeers();
                continue;
            }

            if (line.StartsWith('@'))
            {
                int space = line.IndexOf(' ');
                if (space > 1 && int.TryParse(line[1..space], out int targetPort))
                    await node.SendToAsync($"127.0.0.1:{targetPort}", PeerMessageKind.Chat, line[(space + 1)..]);
                else
                    Console.WriteLine("usage: @<port> <message>");
                continue;
            }

            await node.BroadcastAsync(PeerMessageKind.Chat, line);
        }

        Console.WriteLine("Goodbye.");
    }
}
