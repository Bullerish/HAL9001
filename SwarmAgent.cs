using System.Collections.Concurrent;
using System.Text.Json;

namespace HAL9001;

/// <summary>
/// Rung 3: a swarm node with a COORDINATOR role that routes a question to one handler node
/// and returns the answer. Built on the rungs 1–2 <see cref="SwarmNode"/> transport; the
/// two-node phase-1 agent path is untouched.
///
/// COORDINATOR SELECTION (static, no election yet):
///   The coordinator is simply the LOWEST-port node in the current membership (peers ∪ self).
///   Every node computes this from its own peer list with the identical rule, so they agree
///   without any negotiation. (Leader election — heartbeats, failover, in-flight recovery —
///   is a LATER rung; this rule just recomputes from the lowest remaining port if the
///   coordinator drops, which is where election will later hook in.)
///
/// ASSIGNMENT RULE (deliberately minimal this rung):
///   ROUND-ROBIN over the current members. The coordinator cycles an index through the
///   sorted membership so work spreads evenly across nodes (including itself). Capability-
///   aware / competitive assignment is a later rung — there's no cross-node capability
///   directory yet, so round-robin is the simplest rule that spreads load.
///
/// FULL PATH: A asks the swarm → A sends "ask" to coordinator C → C picks handler B
///   (round-robin) and sends "assign" → B answers via its agent path → B sends "result" to
///   C → C sends "deliver" to A → A displays it (labeled with C and B).
/// Correlation is by a request id carried in every control message; C holds a pending map
/// {reqId → origin asker} so a result finds its way home.
/// </summary>
public static class SwarmAgent
{
    // One JSON envelope for all coordination messages (PeerMessageKind.Swarm).
    private sealed record SwarmMsg(
        string Type, string ReqId,
        string? Question = null, string? Origin = null,
        string? Answer = null, string? AnsweredBy = null, string? Coordinator = null);

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static async Task RunAsync(int myPort, IReadOnlyList<int> peerPorts)
    {
        // ── Agent machinery (same pieces as phase 1; null client = no key = stub answers) ──
        AnthropicClient? client = AnthropicClient.FromEnvironment();
        var registry = new HandlerRegistry();
        GitSync? git = GitSync.Discover();
        if (git is not null)
        {
            git.Pull();
            HandlerLoader.LoadAll(git.HandlersDirectory, registry);
        }
        HandlerGenerator? generator = client is null ? null : new HandlerGenerator(client, registry, git);
        CapabilityRouter? router = client is null ? null : new CapabilityRouter(client, registry);
        var answerGate = new SemaphoreSlim(1, 1);

        await using var node = new SwarmNode(myPort);

        // Coordinator role state: requests we're coordinating, mapped to who asked.
        var pending = new ConcurrentDictionary<string, string>(); // reqId -> origin asker id
        int roundRobin = 0;

        // ── Rung 4a: heartbeat failure detection (NO election yet) ───────────────────────
        // Interval 1s, death timeout 4s (= 4 missed beats). Why 4×: we measure "time since the
        // last beat heard," which already includes up to ~1 interval of pre-existing age, so a
        // deliberate 2s stall can mean ~3s of measured silence. At 3× that trips on a mere 2s
        // transient hiccup (GC pause, scheduler lag, a busy socket) — a false positive, which
        // is the expensive mistake (in 4b it would depose a live coordinator → split-brain). 4×
        // gives a clear ~1s margin over a 2-3s transient stall while still detecting a real
        // death within ~4s. The interval:timeout ratio is the whole knob: lower = faster but
        // more false positives; higher = fewer false positives but slower. 4× is the balance.
        const int HeartbeatIntervalMs = 1000;
        const double DeathTimeoutSeconds = 4.0;

        var hbLock = new object();
        string? watched = null;                    // the coordinator we monitor — STICKY: it does
                                                   // NOT change when a peer merely drops (TCP), only
                                                   // when a lower-port coordinator appears or (4b) an
                                                   // election runs. That stickiness is what lets a
                                                   // hard-killed coordinator be detected by heartbeat
                                                   // rather than silently masked by the rung-3 recompute.
        DateTime lastBeat = DateTime.UtcNow;       // last heartbeat heard from `watched` (grace at start)
        bool suspected = false;                    // are we currently suspecting `watched` is dead
        DateTime pausedUntil = DateTime.MinValue;  // test affordance: simulate a hung coordinator
        using var hbCts = new CancellationTokenSource();

        // ── Coordinator selection + assignment (pure functions over current membership) ───
        List<string> Members() => node.Peers.Append(node.Id).Distinct().OrderBy(PortOf).ToList();
        string Coordinator() => Members()[0]; // lowest port
        string PickHandler()
        {
            var members = Members();
            int i = Interlocked.Increment(ref roundRobin);
            return members[((i % members.Count) + members.Count) % members.Count];
        }

        // ── The agent answer path (route → use/commission/decline → run). Stub if no key. ──
        async Task<string> AnswerAsync(string question)
        {
            if (router is null || generator is null)
                return $"(node {node.Id} has no API key — routed stub; would answer: \"{question}\")";

            await answerGate.WaitAsync();
            try
            {
                RouteDecision decision = await router.RouteAsync(question);
                if (decision.Action == RouteAction.Decline)
                    return decision.Reply;

                IHandler? handler;
                if (decision.Action == RouteAction.UseExisting && registry.TryGet(decision.Name, out handler))
                {
                    Console.WriteLine($"  (using capability '{decision.Name}')");
                }
                else
                {
                    string name = decision.Name.Length > 0 ? decision.Name : "capability";
                    string desc = decision.Description.Length > 0 ? decision.Description : question;
                    Console.WriteLine($"  (commissioning '{name}': {desc})");
                    try { handler = await generator.GenerateAsync(name, desc, question); }
                    catch (Exception ex) { return $"(generation failed: {ex.Message})"; }
                    if (handler is null) return "(couldn't build a working handler)";
                }

                try { return await Task.Run(() => handler!.Handle(question)).WaitAsync(TimeSpan.FromSeconds(30)); }
                catch (TimeoutException) { return "(the capability took too long)"; }
                catch (Exception ex) { return $"(runtime error: {ex.GetBaseException().Message})"; }
            }
            finally { answerGate.Release(); }
        }

        // ── Coordinator: pick a handler and route the question there (or handle locally) ──
        async Task CoordinateAsync(string reqId, string question, string origin)
        {
            string handler = PickHandler();
            Console.WriteLine($"[coordinator] assigning req {reqId} to {handler}");
            if (handler == node.Id)
            {
                string answer = await AnswerAsync(question);
                await DeliverAsync(reqId, answer, node.Id, origin);
            }
            else
            {
                pending[reqId] = origin; // remember who to deliver the result back to
                await node.SendToAsync(handler, PeerMessageKind.Swarm,
                    JsonSerializer.Serialize(new SwarmMsg("assign", reqId, Question: question)));
            }
        }

        // ── Coordinator: send a finished answer back to whoever asked ──
        async Task DeliverAsync(string reqId, string answer, string answeredBy, string origin)
        {
            if (origin == node.Id)
            {
                Console.WriteLine($"\n[swarm answer — handled by {answeredBy}] {answer}");
                Console.Write("> ");
            }
            else
            {
                await node.SendToAsync(origin, PeerMessageKind.Swarm,
                    JsonSerializer.Serialize(new SwarmMsg("deliver", reqId, Answer: answer, AnsweredBy: answeredBy, Coordinator: node.Id)));
            }
        }

        // ── Dispatch incoming swarm-control messages ──
        async Task OnSwarmAsync(string from, string json)
        {
            SwarmMsg? m;
            try { m = JsonSerializer.Deserialize<SwarmMsg>(json, JsonOpts); }
            catch { return; }
            if (m is null) return;

            switch (m.Type)
            {
                case "ask": // I am the coordinator
                    Console.WriteLine($"\n[coordinator] {m.Origin} asked: \"{m.Question}\"");
                    await CoordinateAsync(m.ReqId, m.Question ?? "", m.Origin ?? from);
                    break;

                case "assign": // I was chosen to handle this
                    Console.WriteLine($"\n[assigned by {from}] handling: \"{m.Question}\"");
                    string ans = await AnswerAsync(m.Question ?? "");
                    Console.WriteLine($"  returning answer to coordinator {from}");
                    await node.SendToAsync(from, PeerMessageKind.Swarm,
                        JsonSerializer.Serialize(new SwarmMsg("result", m.ReqId, Answer: ans, AnsweredBy: node.Id)));
                    Console.Write("> ");
                    break;

                case "result": // I am the coordinator, a handler returned
                    if (pending.TryRemove(m.ReqId, out string? origin))
                        await DeliverAsync(m.ReqId, m.Answer ?? "", m.AnsweredBy ?? from, origin);
                    break;

                case "deliver": // I am the original asker
                    Console.WriteLine($"\n[swarm answer ← coordinator {m.Coordinator}, handled by {m.AnsweredBy}] {m.Answer}");
                    Console.Write("> ");
                    break;
            }
        }

        // ── Ask the swarm a question (from the local REPL) ──
        async Task AskSwarmAsync(string question)
        {
            string reqId = Guid.NewGuid().ToString("N")[..8];
            string c = Coordinator();
            Console.WriteLine($"[ask] req {reqId} → coordinator {c}");
            if (c == node.Id)
                await CoordinateAsync(reqId, question, node.Id);          // I'm the coordinator: route locally
            else if (node.Peers.Contains(c))
                await node.SendToAsync(c, PeerMessageKind.Swarm,
                    JsonSerializer.Serialize(new SwarmMsg("ask", reqId, Question: question, Origin: node.Id)));
            else
                Console.WriteLine($"[ask] coordinator {c} not reachable yet — try again in a moment.");
        }

        // ── Rung 4a: heartbeat send / receive / monitor ──────────────────────────────────
        // The lowest-port-among-the-currently-believed-successor would be (used only for the
        // "next would be" print on death; 4b's election will use this).
        string SuccessorOf(string deadCoord)
        {
            var alive = Members().Where(m => m != deadCoord).OrderBy(PortOf).ToList();
            return alive.Count > 0 ? alive[0] : "(none)";
        }

        // A heartbeat arrived from `from` (synchronous state update).
        void OnHeartbeat(string from)
        {
            lock (hbLock)
            {
                if (watched is null || PortOf(from) < PortOf(watched))
                {
                    // A (new/lower-port) coordinator we should follow.
                    watched = from;
                    lastBeat = DateTime.UtcNow;
                    suspected = false;
                }
                else if (from == watched)
                {
                    lastBeat = DateTime.UtcNow;
                    if (suspected)
                    {
                        suspected = false;
                        Console.WriteLine($"\n[detect] coordinator {watched} RECOVERED — heartbeat resumed (was slow, not dead).");
                        Console.Write("> ");
                    }
                }
                // Heartbeat from a higher-port node that isn't our coordinator → ignore.
            }
        }

        // Coordinator beats once per interval. The "current coordinator" is the lowest-port
        // live node, so after a death the new lowest-port node naturally starts beating.
        async Task HeartbeatSenderLoop()
        {
            while (!hbCts.IsCancellationRequested)
            {
                try { await Task.Delay(HeartbeatIntervalMs, hbCts.Token); } catch { break; }
                bool paused;
                lock (hbLock) paused = DateTime.UtcNow < pausedUntil;
                if (paused) continue;                                   // simulated hang: skip beats
                if (Coordinator() == node.Id)
                    await node.BroadcastAsync(PeerMessageKind.Heartbeat, node.Id);
            }
        }

        // Watch the coordinator's heartbeats; declare it suspected-dead past the timeout.
        // DETECTION ONLY — we announce and name the successor, but take NO action (no election,
        // no failover, no in-flight recovery). >>> rung 4b's election hooks in right here. <<<
        async Task MonitorLoop()
        {
            while (!hbCts.IsCancellationRequested)
            {
                try { await Task.Delay(1000, hbCts.Token); } catch { break; }
                string me = node.Id;
                string current = Coordinator();
                lock (hbLock)
                {
                    watched ??= current; // lazy init (a fresh lone node watches itself; corrected on first lower beat)
                    if (watched != me)
                    {
                        double silence = (DateTime.UtcNow - lastBeat).TotalSeconds;
                        if (!suspected && silence > DeathTimeoutSeconds)
                        {
                            suspected = true;
                            Console.WriteLine($"\n[detect] coordinator {watched} SUSPECTED DEAD — no heartbeat for ~{silence:F0}s. " +
                                              $"Next by lowest-port would be {SuccessorOf(watched)}. (rung 4b would elect here — no action taken.)");
                            Console.Write("> ");
                        }
                    }
                }
            }
        }

        // ── Wire up + run ──
        node.MessageReceived += (from, msg) =>
        {
            switch (msg.Kind)
            {
                case PeerMessageKind.Swarm: _ = SafeOnSwarm(from, msg.Text); break;
                case PeerMessageKind.Heartbeat: OnHeartbeat(from); break;
                case PeerMessageKind.Chat: Console.WriteLine($"\n[from {from}] {msg.Text}"); Console.Write("> "); break;
            }
        };
        node.MembershipChanged += () =>
            Console.WriteLine($"[coordinator?] I believe the coordinator is {Coordinator()}");

        async Task SafeOnSwarm(string from, string json)
        {
            try { await OnSwarmAsync(from, json); }
            catch (Exception ex) { Console.WriteLine($"[swarm] handling error: {ex.Message}"); }
        }

        await node.StartAsync(peerPorts);
        _ = HeartbeatSenderLoop();
        _ = MonitorLoop();

        Console.WriteLine();
        Console.WriteLine($"Swarm-agent {node.Id}. Coordinator (computed): {Coordinator()}." +
                          (client is null ? " (no API key — this node answers with stubs.)" : ""));
        Console.WriteLine("Commands:  <question>     — ask the SWARM (routed via coordinator)");
        Console.WriteLine("           @<port> <msg>  — message one peer directly (rung 1)");
        Console.WriteLine("           peers          — my peer list");
        Console.WriteLine("           coordinator    — who I think the coordinator is");
        Console.WriteLine("           pause <secs>   — stop my heartbeats briefly (simulate a hung coordinator)");
        Console.WriteLine("           exit           — quit");
        Console.WriteLine();

        while (true)
        {
            Console.Write("> ");
            string? line = Console.ReadLine();
            if (line is null || line.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase)) break;

            line = line.Trim();
            if (line.Length == 0) continue;

            if (line.Equals("peers", StringComparison.OrdinalIgnoreCase)) { node.PrintPeers(); continue; }
            if (line.Equals("coordinator", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine($"coordinator = {Coordinator()}"); continue; }
            if (line.StartsWith("pause ", StringComparison.OrdinalIgnoreCase) && int.TryParse(line[6..].Trim(), out int secs))
            {
                lock (hbLock) pausedUntil = DateTime.UtcNow.AddSeconds(secs);
                Console.WriteLine($"[test] pausing my heartbeats for {secs}s (simulating a hung coordinator)");
                continue;
            }
            if (line.StartsWith('@'))
            {
                int space = line.IndexOf(' ');
                if (space > 1 && int.TryParse(line[1..space], out int tp))
                    await node.SendToAsync($"127.0.0.1:{tp}", PeerMessageKind.Chat, line[(space + 1)..]);
                else
                    Console.WriteLine("usage: @<port> <message>");
                continue;
            }

            await AskSwarmAsync(line); // anything else = ask the swarm
        }

        hbCts.Cancel(); // stop heartbeat + monitor loops before tearing the node down
        Console.WriteLine("Goodbye.");
    }

    private static int PortOf(string id)
    {
        int colon = id.LastIndexOf(':');
        return colon >= 0 && int.TryParse(id[(colon + 1)..], out int p) ? p : -1;
    }
}
