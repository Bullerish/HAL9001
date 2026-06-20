using HAL9001;

// =====================================================================================
// HAL9001 entry point. Dispatches to a demo based on command-line arguments:
//
//   (no args) | demo        Step 1 — Roslyn runtime compile-and-load demo (+ REPL)
//   host <port>             Step 2 — TCP peer: listen for a peer on <port>
//   join <host> <port>      Step 2 — TCP peer: connect to a peer at <host>:<port>
//
// Run two instances to see Step 2:  one `host 5000`, one `join 127.0.0.1 5000`.
// =====================================================================================

// Default (or explicit "demo") → the Step 1 Roslyn demo.
if (args.Length == 0 || args[0].Equals("demo", StringComparison.OrdinalIgnoreCase))
{
    RoslynDemo.Run();
    return;
}

switch (args[0].ToLowerInvariant())
{
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
        Console.WriteLine("  HAL9001                     Run the Step 1 Roslyn demo (default)");
        Console.WriteLine("  HAL9001 host <port>         Listen for a peer on <port>");
        Console.WriteLine("  HAL9001 join <host> <port>  Connect to a peer at <host>:<port>");
        break;
}
