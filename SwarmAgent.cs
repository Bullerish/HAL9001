using System.Collections.Concurrent;
using System.Text.Json;

namespace HAL9001;

/// <summary>
/// Swarm node with an ELECTED coordinator (rungs 3 → 4b-i). Built on the rungs 1–2
/// <see cref="SwarmNode"/> transport; the two-node phase-1 path is untouched.
///
/// COORDINATOR = an elected, TERM-STAMPED role (not just "whatever's lowest-port right now").
///   • Bootstrap (term 0): every node starts believing it's the coordinator and beats; the
///     "adopt a lower-port beat at the same term" rule converges everyone to the lowest port,
///     with no election needed.
///   • Failover (term N→N+1): when heartbeat detection (rung 4a) declares the coordinator dead,
///     the lowest-port LIVE node runs a bully election and only takes the role once a QUORUM
///     (majority of known members) has voted for it — that's what makes exactly one leader.
///
/// QUORUM / NO SPLIT-BRAIN: a candidate becomes leader only with votes from a majority of
///   `_allKnown` (the high-water set of members ever seen — a STABLE denominator that doesn't
///   shrink when nodes drop). Each node casts at most one vote per term, and any two majorities
///   of the same set overlap, so two candidates can't both reach majority in a term → at most
///   one leader per term. A minority partition can't reach majority of `_allKnown`, so it can't
///   elect at all → no second coordinator.
///
/// RETURNING COORDINATOR steps down via TERMS: the old leader comes back stamped with an old
///   term; any node it talks to answers with the current (higher) term + leader, and a higher
///   term always wins, so the returnee adopts the new leader and yields instead of re-asserting.
///
/// In-flight work recovery is NOT here — a question mid-flight when the coordinator died may be
/// dropped this rung. >>> rung 4b-ii hooks in where marked. <<<
/// </summary>
public static class SwarmAgent
{
    private sealed record SwarmMsg(
        string Type, string ReqId = "",
        string? Question = null, string? Origin = null,
        string? Answer = null, string? AnsweredBy = null, string? Coordinator = null,
        int Term = 0, string? Candidate = null, string? Voter = null);

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static async Task RunAsync(int myPort, IReadOnlyList<int> peerPorts)
    {
        AnthropicClient? client = AnthropicClient.FromEnvironment();
        var registry = new HandlerRegistry();
        GitSync? git = GitSync.Discover();
        if (git is not null) { git.Pull(); HandlerLoader.LoadAll(git.HandlersDirectory, registry); }
        HandlerGenerator? generator = client is null ? null : new HandlerGenerator(client, registry, git);
        CapabilityRouter? router = client is null ? null : new CapabilityRouter(client, registry);
        var answerGate = new SemaphoreSlim(1, 1);

        await using var node = new SwarmNode(myPort);
        var pending = new ConcurrentDictionary<string, string>(); // reqId -> origin asker (coordinator role)
        int roundRobin = 0;

        // ── Heartbeat + election timing (rung 4a/4b) ──────────────────────────────────────
        const int HeartbeatIntervalMs = 1000;
        const double DeathTimeoutSeconds = 4.0; // 4× interval; see rung 4a for the ratio rationale

        // All coordinator/election state lives behind one lock (touched by the receive thread,
        // the monitor loop, and the heartbeat loop).
        var stateLock = new object();
        int term = 0;                                  // current election term we believe in
        string coordinator = node.Id;                  // current coordinator (elected/agreed)
        DateTime lastBeat = DateTime.UtcNow;           // last heartbeat from `coordinator`
        bool suspected = false;                        // do we currently suspect `coordinator` dead
        int campaignTerm = 0;                          // term I'm campaigning for (0 = not campaigning)
        var votes = new HashSet<string>();             // votes received for my campaign
        var votedFor = new Dictionary<int, string>();  // term -> candidate I voted for (one vote/term)
        DateTime pausedUntil = DateTime.MinValue;      // test: simulate a hung coordinator
        // STABLE quorum denominator: every member ever seen. Grows only — so a minority can't
        // shrink the set to reach a false majority.
        var allKnown = new HashSet<string>(peerPorts.Select(p => $"127.0.0.1:{p}")) { node.Id };

        using var loopCts = new CancellationTokenSource();

        int PortOf(string id) { int c = id.LastIndexOf(':'); return c >= 0 && int.TryParse(id[(c + 1)..], out int p) ? p : -1; }
        List<string> LiveMembers() => node.Peers.Append(node.Id).Distinct().ToList();
        string LowestAlive(string? exclude) =>
            LiveMembers().Where(m => m != exclude).DefaultIfEmpty(node.Id).OrderBy(PortOf).First();
        int Majority() { lock (stateLock) return allKnown.Count / 2 + 1; }

        Task SendSwarm(string to, SwarmMsg m) => node.SendToAsync(to, PeerMessageKind.Swarm, JsonSerializer.Serialize(m));
        Task BroadcastSwarm(SwarmMsg m) => node.BroadcastAsync(PeerMessageKind.Swarm, JsonSerializer.Serialize(m));

        // ── The agent answer path (route → use/commission/decline → run). Stub if no key. ──
        async Task<string> AnswerAsync(string question)
        {
            if (router is null || generator is null)
                return $"(node {node.Id} has no API key — routed stub; would answer: \"{question}\")";
            await answerGate.WaitAsync();
            try
            {
                RouteDecision decision = await router.RouteAsync(question);
                if (decision.Action == RouteAction.Decline) return decision.Reply;

                IHandler? handler;
                if (decision.Action == RouteAction.UseExisting && registry.TryGet(decision.Name, out handler))
                    Console.WriteLine($"  (using capability '{decision.Name}')");
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

        // ── Routing (rung 3), now targeting the ELECTED coordinator ──
        string PickHandler()
        {
            var members = LiveMembers().OrderBy(PortOf).ToList();
            int i = Interlocked.Increment(ref roundRobin);
            return members[((i % members.Count) + members.Count) % members.Count];
        }

        async Task DeliverAsync(string reqId, string answer, string answeredBy, string origin)
        {
            if (origin == node.Id) { Console.WriteLine($"\n[swarm answer — handled by {answeredBy}] {answer}"); Console.Write("> "); }
            else await SendSwarm(origin, new SwarmMsg("deliver", reqId, Answer: answer, AnsweredBy: answeredBy, Coordinator: node.Id));
        }

        async Task CoordinateAsync(string reqId, string question, string origin)
        {
            string handler = PickHandler();
            Console.WriteLine($"[coordinator] assigning req {reqId} to {handler}");
            if (handler == node.Id) { string a = await AnswerAsync(question); await DeliverAsync(reqId, a, node.Id, origin); }
            else { pending[reqId] = origin; await SendSwarm(handler, new SwarmMsg("assign", reqId, Question: question)); }
            // >>> rung 4b-ii: track this assignment so it can be re-driven if the handler/coordinator dies. <<<
        }

        async Task AskSwarmAsync(string question)
        {
            string reqId = Guid.NewGuid().ToString("N")[..8];
            string c; lock (stateLock) c = coordinator;
            Console.WriteLine($"[ask] req {reqId} → coordinator {c}");
            if (c == node.Id) await CoordinateAsync(reqId, question, node.Id);
            else if (node.Peers.Contains(c)) await SendSwarm(c, new SwarmMsg("ask", reqId, Question: question, Origin: node.Id));
            else Console.WriteLine($"[ask] coordinator {c} not reachable yet — try again in a moment.");
        }

        // ── Election ──
        async Task BeginCampaignAsync(int t)
        {
            bool start = false;
            lock (stateLock)
            {
                if (campaignTerm < t) { campaignTerm = t; votes.Clear(); votes.Add(node.Id); votedFor[t] = node.Id; start = true; }
            }
            if (!start) return;
            Console.WriteLine($"\n[election] running for coordinator (term {t})");
            Console.Write("> ");
            await BroadcastSwarm(new SwarmMsg("elect", Term: t, Candidate: node.Id));
            await TryWinAsync(t); // a 1-node majority would win immediately
        }

        async Task TryWinAsync(int t)
        {
            bool won = false; int got = 0, need = Majority();
            lock (stateLock)
            {
                if (campaignTerm == t && votes.Count >= need)
                {
                    won = true; got = votes.Count;
                    coordinator = node.Id; term = t; campaignTerm = 0; suspected = false; lastBeat = DateTime.UtcNow;
                }
            }
            if (won)
            {
                Console.WriteLine($"\n[election] WON term {t} with {got}/{Majority()} votes — I am the coordinator now.");
                Console.Write("> ");
                await BroadcastSwarm(new SwarmMsg("leader", Term: t, Coordinator: node.Id));
            }
        }

        // ── Swarm message dispatch (routing + election) ──
        async Task OnSwarmAsync(string from, string json)
        {
            SwarmMsg? m; try { m = JsonSerializer.Deserialize<SwarmMsg>(json, JsonOpts); } catch { return; }
            if (m is null) return;
            lock (stateLock) allKnown.Add(from);

            switch (m.Type)
            {
                case "ask":
                    Console.WriteLine($"\n[coordinator] {m.Origin} asked: \"{m.Question}\"");
                    await CoordinateAsync(m.ReqId, m.Question ?? "", m.Origin ?? from);
                    break;
                case "assign":
                    Console.WriteLine($"\n[assigned by {from}] handling: \"{m.Question}\"");
                    string ans = await AnswerAsync(m.Question ?? "");
                    await SendSwarm(from, new SwarmMsg("result", m.ReqId, Answer: ans, AnsweredBy: node.Id));
                    Console.Write("> ");
                    break;
                case "result":
                    if (pending.TryRemove(m.ReqId, out string? origin)) await DeliverAsync(m.ReqId, m.Answer ?? "", m.AnsweredBy ?? from, origin);
                    break;
                case "deliver":
                    Console.WriteLine($"\n[swarm answer ← coordinator {m.Coordinator}, handled by {m.AnsweredBy}] {m.Answer}");
                    Console.Write("> ");
                    break;

                case "elect": // a candidate is running for term m.Term
                {
                    bool doVote = false; string cand = m.Candidate ?? from;
                    lock (stateLock)
                    {
                        allKnown.Add(cand);
                        bool oldLeaderGone = suspected || !LiveMembers().Contains(coordinator);
                        string shouldBe = LowestAlive(exclude: suspected ? coordinator : null);
                        if (m.Term > term && !votedFor.ContainsKey(m.Term) && oldLeaderGone && cand == shouldBe)
                        { votedFor[m.Term] = cand; doVote = true; }
                    }
                    if (doVote)
                    {
                        Console.WriteLine($"\n[election] voting for {cand} (term {m.Term})"); Console.Write("> ");
                        await SendSwarm(cand, new SwarmMsg("vote", Term: m.Term, Candidate: cand, Voter: node.Id));
                    }
                    break;
                }
                case "vote": // I'm a candidate; tally
                    lock (stateLock) { if (campaignTerm == m.Term && m.Candidate == node.Id) votes.Add(m.Voter ?? from); }
                    await TryWinAsync(m.Term);
                    break;

                case "leader": // someone reached quorum and is the leader for m.Term
                {
                    bool adopt = false;
                    lock (stateLock)
                    {
                        if (m.Term >= term && m.Coordinator is not null)
                        { term = m.Term; coordinator = m.Coordinator; campaignTerm = 0; suspected = false; lastBeat = DateTime.UtcNow; votes.Clear(); adopt = true; }
                    }
                    if (adopt) { Console.WriteLine($"\n[election] coordinator is now {m.Coordinator} (term {m.Term})"); Console.Write("> "); }
                    break;
                }
            }
        }

        // ── Heartbeats ──
        void OnHeartbeat(string from, int fromTerm)
        {
            string? nudgeTo = null; int nudgeTerm = 0; string? nudgeLeader = null;
            string? announce = null;
            lock (stateLock)
            {
                allKnown.Add(from);
                if (fromTerm > term) // a newer leader — adopt and (if I thought I led) step down
                {
                    term = fromTerm; coordinator = from; lastBeat = DateTime.UtcNow; suspected = false; campaignTerm = 0;
                    announce = $"[election] following coordinator {from} (term {fromTerm})";
                }
                else if (fromTerm == term)
                {
                    if (from == coordinator) { lastBeat = DateTime.UtcNow; if (suspected) { suspected = false; announce = $"[detect] coordinator {coordinator} RECOVERED — heartbeat resumed (was slow, not dead)."; } }
                    else if (PortOf(from) < PortOf(coordinator)) { coordinator = from; lastBeat = DateTime.UtcNow; suspected = false; announce = $"[election] coordinator is now {from} (term {term})"; } // term-0 bootstrap convergence
                }
                else // fromTerm < term: a STALE leader (e.g. a returning old coordinator) — tell it the truth
                { nudgeTo = from; nudgeTerm = term; nudgeLeader = coordinator; }
            }
            if (announce is not null) { Console.WriteLine($"\n{announce}"); Console.Write("> "); }
            if (nudgeTo is not null) _ = SendSwarm(nudgeTo, new SwarmMsg("leader", Term: nudgeTerm, Coordinator: nudgeLeader));
        }

        async Task HeartbeatSenderLoop()
        {
            while (!loopCts.IsCancellationRequested)
            {
                try { await Task.Delay(HeartbeatIntervalMs, loopCts.Token); } catch { break; }
                int t; bool amLeader, paused;
                lock (stateLock) { t = term; amLeader = coordinator == node.Id; paused = DateTime.UtcNow < pausedUntil; }
                if (amLeader && !paused) await node.BroadcastAsync(PeerMessageKind.Heartbeat, t.ToString());
            }
        }

        // Detect coordinator death and, where 4a only announced, now TRIGGER AN ELECTION.
        async Task MonitorLoop()
        {
            while (!loopCts.IsCancellationRequested)
            {
                try { await Task.Delay(1000, loopCts.Token); } catch { break; }
                bool campaign = false; int campaignFor = 0; string? deadCoord = null;
                lock (stateLock)
                {
                    if (coordinator != node.Id && (DateTime.UtcNow - lastBeat).TotalSeconds > DeathTimeoutSeconds && !suspected)
                    {
                        suspected = true;
                        deadCoord = coordinator;
                        string candidate = LowestAlive(exclude: coordinator); // lowest-port LIVE node, excluding the dead one
                        if (candidate == node.Id) { campaign = true; campaignFor = term + 1; }
                    }
                }
                if (deadCoord is not null && !campaign)
                    { Console.WriteLine($"\n[detect] coordinator {deadCoord} SUSPECTED DEAD — awaiting election (candidate {LowestAlive(deadCoord)})."); Console.Write("> "); }
                if (campaign) { Console.WriteLine($"\n[detect] coordinator {deadCoord} SUSPECTED DEAD — I'm lowest-port alive, starting election."); await BeginCampaignAsync(campaignFor); }
            }
        }

        // ── Wire up + run ──
        node.MessageReceived += (from, msg) =>
        {
            switch (msg.Kind)
            {
                case PeerMessageKind.Swarm: _ = SafeOnSwarm(from, msg.Text); break;
                case PeerMessageKind.Heartbeat: if (int.TryParse(msg.Text, out int ht)) OnHeartbeat(from, ht); break;
                case PeerMessageKind.Chat: Console.WriteLine($"\n[from {from}] {msg.Text}"); Console.Write("> "); break;
            }
        };
        node.MembershipChanged += () =>
        {
            lock (stateLock) foreach (var p in node.Peers) allKnown.Add(p);
            string c; lock (stateLock) c = coordinator;
            Console.WriteLine($"[swarm] coordinator (believed): {c}");
        };
        async Task SafeOnSwarm(string from, string json)
        { try { await OnSwarmAsync(from, json); } catch (Exception ex) { Console.WriteLine($"[swarm] handling error: {ex.Message}"); } }

        await node.StartAsync(peerPorts);
        _ = HeartbeatSenderLoop();
        _ = MonitorLoop();

        Console.WriteLine();
        Console.WriteLine($"Swarm-agent {node.Id}." + (client is null ? " (no API key — answers with stubs.)" : ""));
        Console.WriteLine("Commands:  <question>   ask the swarm   |   @<port> <msg>  direct   |   peers   |   coordinator   |   pause <secs>   |   exit");
        Console.WriteLine();

        while (true)
        {
            Console.Write("> ");
            string? line = Console.ReadLine();
            if (line is null || line.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase)) break;
            line = line.Trim();
            if (line.Length == 0) continue;
            if (line.Equals("peers", StringComparison.OrdinalIgnoreCase)) { node.PrintPeers(); continue; }
            if (line.Equals("coordinator", StringComparison.OrdinalIgnoreCase)) { lock (stateLock) Console.WriteLine($"coordinator = {coordinator} (term {term})"); continue; }
            if (line.StartsWith("pause ", StringComparison.OrdinalIgnoreCase) && int.TryParse(line[6..].Trim(), out int secs))
            { lock (stateLock) pausedUntil = DateTime.UtcNow.AddSeconds(secs); Console.WriteLine($"[test] pausing my heartbeats for {secs}s (simulating a hung coordinator)"); continue; }
            if (line.StartsWith('@'))
            {
                int sp = line.IndexOf(' ');
                if (sp > 1 && int.TryParse(line[1..sp], out int tp)) await node.SendToAsync($"127.0.0.1:{tp}", PeerMessageKind.Chat, line[(sp + 1)..]);
                else Console.WriteLine("usage: @<port> <message>");
                continue;
            }
            await AskSwarmAsync(line);
        }

        loopCts.Cancel();
        Console.WriteLine("Goodbye.");
    }
}
