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

        // ── Wire up + run ──
        node.MessageReceived += (from, msg) =>
        {
            switch (msg.Kind)
            {
                case PeerMessageKind.Swarm: _ = SafeOnSwarm(from, msg.Text); break;
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

        Console.WriteLine();
        Console.WriteLine($"Swarm-agent {node.Id}. Coordinator (computed): {Coordinator()}." +
                          (client is null ? " (no API key — this node answers with stubs.)" : ""));
        Console.WriteLine("Commands:  <question>     — ask the SWARM (routed via coordinator)");
        Console.WriteLine("           @<port> <msg>  — message one peer directly (rung 1)");
        Console.WriteLine("           peers          — my peer list");
        Console.WriteLine("           coordinator    — who I think the coordinator is");
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

        Console.WriteLine("Goodbye.");
    }

    private static int PortOf(string id)
    {
        int colon = id.LastIndexOf(':');
        return colon >= 0 && int.TryParse(id[(colon + 1)..], out int p) ? p : -1;
    }
}
