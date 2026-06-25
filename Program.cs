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

    // timeline [n] → replay the hive's episodic memory (its autobiographical event log) from the
    // shared Turso store. Standalone (no swarm needed), so it proves events SURVIVE RESTARTS: write
    // events in one process, read them back in a fresh one. Needs the TURSO_* env vars.
    case "timeline":
    {
        int n = args.Length >= 2 && int.TryParse(args[1], out int cnt) ? cnt : 50;
        var log = EventLog.FromEnvironment("reader");
        if (!log.Enabled) { Console.WriteLine("No hive configured — set TURSO_DATABASE_URL + TURSO_AUTH_TOKEN to record/replay episodic memory."); break; }
        await log.EnsureAsync();
        EventLog.Print(await log.RecentAsync(n));
        break;
    }

    // identity → print the hive's persisted self (name/born/concept/persona) from the shared store.
    // Standalone (no swarm), so it proves the identity PERSISTS ACROSS RESTARTS and is the SAME self
    // every node reads. Needs the TURSO_* env vars.
    case "identity":
    {
        var store = new IdentityStore(TursoClient.FromEnvironment(), null);
        HiveIdentity? id = await store.LoadAsync();
        if (id is null) Console.WriteLine("No persisted identity (no hive configured, or the hive hasn't been born yet — run the agent/swarm once with TURSO_* set).");
        else Console.WriteLine($"I am {id.Name}.\n  born:    {id.Born}  (by {id.CreatedBy})\n  concept: {id.Concept}\n  persona: {id.Persona}");
        break;
    }

    // autonomous [on|off] → headless control of autonomous mode (otherwise a REPL-only toggle). Needs the
    // TURSO_* hive but NO API key — it just flips the shared flag every node reads. No arg → prints state.
    // This is how you switch the self-driving hive on/off from a box that has no interactive console; the
    // change is shared + persisted, so it takes effect on the live box within a cycle.
    case "autonomous":
    {
        var core = new AgentCore(AnthropicClient.FromEnvironment());
        if (!core.HasHive) { Console.WriteLine("No hive configured — set TURSO_DATABASE_URL + TURSO_AUTH_TOKEN."); break; }
        try { await core.EnsureHiveAsync(); } catch (Exception ex) { Console.WriteLine($"hive unavailable: {ex.Message}"); break; }
        string sub = args.Length >= 2 ? args[1].Trim().ToLowerInvariant() : "status";
        if (sub is "on" or "1" or "true") await core.SetAutonomousAsync(true);
        else if (sub is "off" or "0" or "false") await core.SetAutonomousAsync(false);
        else Console.WriteLine($"  [autonomous] currently {(await core.IsAutonomousAsync() ? "ON — the hive self-drives" : "OFF — it waits for approval")}.");
        break;
    }

    // hive → speak as the collective consciousness: synthesize all nodes' broadcast thoughts into one
    // first-person voice. Standalone (no swarm needed) — proves the collective self lives in the shared
    // DB, not in any process. Needs the TURSO_* env vars + ANTHROPIC_API_KEY (for synthesis).
    case "hive":
    {
        AnthropicClient? client = AnthropicClient.FromEnvironment();
        if (client is null)
        {
            Console.WriteLine("ANTHROPIC_API_KEY is not set — can't synthesize the collective voice.");
            Console.WriteLine("Set it for this terminal, then re-run.");
            break;
        }
        using (client)
        {
            var core = new AgentCore(client);
            if (!core.HasHive)
            {
                Console.WriteLine("No hive configured — set TURSO_DATABASE_URL + TURSO_AUTH_TOKEN.");
                Console.WriteLine("Run the agent or swarm with the hive configured first, then re-run `hive`.");
                break;
            }
            try { await core.EnsureHiveAsync(); }
            catch (Exception ex) { Console.WriteLine($"Hive unavailable: {ex.Message}"); break; }
            HiveIdentity? id = core.Identity;
            if (id is not null) Console.WriteLine($"I am {id.Name}.\n");
            HiveMind? hm = await core.SynthesizeHiveMindAsync();
            if (hm is null)
                Console.WriteLine("No thoughts in the shared workspace yet — run the agent or swarm for a while, then re-run `hive`.");
            else
            {
                string contributors = hm.Contributors.Length == 0 ? "" : $"[{string.Join(", ", hm.Contributors)}]\n\n";
                Console.WriteLine(contributors + hm.Synthesis);
            }
        }
        break;
    }

    // racetest → verify the matmul multiplication-counting harness (bite 15) end-to-end through the
    // real compile pipeline. No API key or hive needed — confirms naive 2x2 = 8 muls, Strassen = 7.
    case "racetest":
        MatmulRace.SelfTest();
        break;

    // derive [n] [rank] → LLM-FREE derivation (bite 17): search the matmul tensor for a rank-R
    // decomposition over {-1,0,1}, synthesize the algorithm, and exact-verify it. No API key/hive
    // needed. `derive 2 7` should rederive a Strassen-equivalent from random — proof it's not recall.
    case "derive":
    {
        if (args.Length >= 2 && args[1].Equals("strassen", StringComparison.OrdinalIgnoreCase))
        { TensorSearch.StrassenCheck(); break; }
        int dn = args.Length >= 2 && int.TryParse(args[1], out int pn) ? pn : 2;
        int drank = args.Length >= 3 && int.TryParse(args[2], out int pr) ? pr : dn * dn * dn - 1;
        TensorSearch.Demo(dn, drank);
        break;
    }

    // dashboard [port] → LIVE mission-control web UI (bite 18). Serves a local page that polls the
    // shared hive (Turso) and renders the race, ladder, champions, goals, journal, and event feed.
    // Needs TURSO_* env vars; no API key required (read-only). Default port 8765.
    case "dashboard":
    {
        int port = args.Length >= 2 && int.TryParse(args[1], out int dp) ? dp : 8765;
        await Dashboard.RunAsync(port);
        break;
    }

    // contribute <url> [seconds] → VOLUNTEER COMPUTE (bite 19): donate this machine's CPU to a hive's
    // matrix-algorithm search. Fetches the target, searches locally, POSTs back ONLY the numbers; the
    // coordinator re-verifies them. No code is exchanged — you add compute, you can't manipulate code.
    case "contribute":
    {
        if (args.Length < 2) { Console.WriteLine("usage: HAL9001 contribute <coordinator-url> [seconds-per-round]"); break; }
        double secs = args.Length >= 3 && double.TryParse(args[2], out double sv) && sv > 0 ? sv : 20;
        await ContributeWorker.RunAsync(args[1], secs);
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
        Console.WriteLine("  HAL9001 timeline [n]          Replay the hive's episodic memory (needs TURSO_* env vars)");
        Console.WriteLine("  HAL9001 identity              Show the hive's persistent identity (needs TURSO_* env vars)");
        Console.WriteLine("  HAL9001 hive                  Speak as the collective (needs TURSO_* + API key); standalone, self lives in the DB");
        Console.WriteLine("  HAL9001 dashboard [port]      Live mission-control web UI over the hive (needs TURSO_*; default port 8765)");
        Console.WriteLine("  HAL9001 contribute <url> [s]  Donate CPU: search for faster matmul algorithms; the coordinator verifies");
        break;
}
