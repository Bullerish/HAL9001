using HAL9001;

// =====================================================================================
// HAL9001 entry point. Dispatches to a mode based on command-line arguments:
//
//   (no args) | agent     Step 3 — self-extending agent REPL (LLM writes & compiles
//                         handlers on demand). Needs ANTHROPIC_API_KEY set.
//   demo                  Step 1 — Roslyn runtime compile-and-load demo (+ REPL)
//   host <port>           Step 2 — TCP peer: listen for a peer on <port>
//   join <host> <port>    Step 2 — TCP peer: connect to a peer at <host>:<port>
// =====================================================================================

// Default (or explicit "agent") → the self-extending agent. Optional peer link:
//   agent                      no peer (single instance)
//   agent host <port>          also listen for a peer on <port>
//   agent join <host> <port>   also connect to a peer at <host>:<port>
if (args.Length == 0 || args[0].Equals("agent", StringComparison.OrdinalIgnoreCase))
{
    PeerEndpoint? endpoint = null;
    if (args.Length >= 2)
    {
        string sub = args[1].ToLowerInvariant();
        if (sub == "host" && args.Length == 3 && int.TryParse(args[2], out int hostPort))
            endpoint = new PeerEndpoint(IsHost: true, RemoteHost: "", Port: hostPort);
        else if (sub == "join" && args.Length == 4 && int.TryParse(args[3], out int joinPort))
            endpoint = new PeerEndpoint(IsHost: false, RemoteHost: args[2], Port: joinPort);
        else
        {
            Console.WriteLine("Usage: HAL9001 agent host <port>  |  HAL9001 agent join <host> <port>");
            return;
        }
    }
    await AgentRepl.RunAsync(endpoint);
    return;
}

switch (args[0].ToLowerInvariant())
{
    // swarm <myPort> [peerPort ...] → rung-1 multi-peer connectivity
    case "swarm" when args.Length >= 2 && int.TryParse(args[1], out int myPort):
    {
        var peerPorts = args.Skip(2)
            .Select(a => int.TryParse(a, out int p) ? p : -1)
            .Where(p => p > 0)
            .ToList();
        await SwarmAgent.RunAsync(myPort, peerPorts);
        break;
    }

    // kernel [size] [candidates] → Kernel Optimization Search bite 1 (single node):
    // generate matmul candidates, verify correctness, benchmark, rank. Needs ANTHROPIC_API_KEY.
    case "kernel":
    {
        int size = args.Length >= 2 && int.TryParse(args[1], out int s) ? s : 256;
        int candidates = args.Length >= 3 && int.TryParse(args[2], out int c) ? c : 5;
        await KernelOptimizer.RunAsync(size, candidates);
        break;
    }

    // demo → Step 1 Roslyn compile-and-load demo
    case "demo":
        RoslynDemo.Run();
        break;

    // host <port>
    case "host" when args.Length == 2 && int.TryParse(args[1], out int hostPort):
        await PeerDemo.RunHostAsync(hostPort);
        break;

    // join <host> <port>
    case "join" when args.Length == 3 && int.TryParse(args[2], out int joinPort):
        await PeerDemo.RunJoinAsync(args[1], joinPort);
        break;

    default:
        Console.WriteLine("Usage:");
        Console.WriteLine("  HAL9001                       Self-extending agent REPL (default; needs ANTHROPIC_API_KEY)");
        Console.WriteLine("  HAL9001 agent host <port>     Agent + listen for a peer on <port>");
        Console.WriteLine("  HAL9001 agent join <host> <p> Agent + connect to a peer at <host>:<p>");
        Console.WriteLine("  HAL9001 demo                  Step 1 Roslyn compile-and-load demo");
        Console.WriteLine("  HAL9001 host <port>           Step 2 TCP chat: listen on <port>");
        Console.WriteLine("  HAL9001 join <host> <port>    Step 2 TCP chat: connect to <host>:<port>");
        Console.WriteLine("  HAL9001 swarm <port> [ports]  Swarm-agent: mesh + ask-the-swarm via coordinator");
        Console.WriteLine("  HAL9001 kernel [size] [n]     Kernel-opt search: generate/verify/benchmark/rank matmul");
        break;
}
