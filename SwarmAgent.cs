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
/// IN-FLIGHT WORK RECOVERY (rung 4b-ii) — a question mid-flight when the coordinator dies must
/// still get answered, exactly once as observed by the asker, without double-generating handlers:
///   • ASKER-SIDE TRACKING (chosen over coordinator state replication): the asker keeps its own
///     `outstanding` map, each entry stamped with the coordinator it was sent to. The PRIMARY
///     recovery trigger is a COORDINATOR CHANGE — when an election installs a new coordinator, the
///     asker immediately re-asks it (fires ~1s after the election, so the answer comes home seconds
///     after a failover). A 30s timeout is a fallback for a lost message with no election. The
///     asker is the one node that authoritatively knows it never got an answer, so no request state
///     needs to survive on the dead coordinator. Capped so an unanswerable question can't loop.
///   • HANDLER REDIRECT: a handler that was mid-generation when the coordinator died sends its
///     result to whoever is coordinator *now* (re-resolved at send time), not the dead node that
///     assigned it. So work already in progress is recovered, not dropped — and the asker usually
///     gets its answer from that original work before its re-ask window even elapses, so no second
///     generation happens.
///   • DEDUP (reqId-keyed): every coordinator keeps a `pending` guard (a reqId already being
///     worked is not re-assigned) and a `doneAnswers` cache (a reqId already answered is served
///     from cache, and a second result for it is dropped). The asker clears `outstanding` the
///     instant it sees an answer, so a question answered just before the coordinator died is never
///     re-asked. GUARANTEE: exactly-once *delivery to the asker*; at-least-once *generation* with
///     dedup that makes the common failover case exactly-once (see the README-style notes inline).
/// </summary>
public static class SwarmAgent
{
    private sealed record SwarmMsg(
        string Type, string ReqId = "",
        string? Question = null, string? Origin = null,
        string? Answer = null, string? AnsweredBy = null, string? Coordinator = null,
        int Term = 0, string? Candidate = null, string? Voter = null,
        // rung 5a/5b fan-out fields:
        string? TestCases = null,    // candreq: JSON array of TestCase the coordinator generated
        string? TestResults = null,  // candidate: JSON array of TestResult the member produced
        string? CapName = null,      // candidate: the capability the member used/built
        string? CapDesc = null,      // candidate: that capability's description (for the winner push)
        string? Source = null,       // candidate: the generated source (only the winner's is pushed, 5b)
        string? Status = null,       // candidate: Ok / Declined / GenerationFailed
        string? InType = null,       // candreq: declared input type every candidate must target (typed rung)
        string? OutType = null);     // candreq: declared output type

    // rung 5a: the coordinator's in-progress fan-out — collecting competing candidates for one reqId.
    private sealed class Competition
    {
        public readonly string Origin;
        public readonly string Question;
        public readonly int Expected;             // members at fan-out time (how many candidates we hope for)
        public readonly CapType InputType;        // declared types fixed for this deliberation (typed rung)
        public readonly CapType OutputType;
        public readonly List<CollectedCandidate> Collected = new();
        public bool Finalized;
        public Competition(string origin, string question, int expected, CapType inputType, CapType outputType)
        { Origin = origin; Question = question; Expected = expected; InputType = inputType; OutputType = outputType; }
    }

    private sealed record CollectedCandidate(
        string Member, string Status, string? Capability, string? Description, string Source,
        string Answer, IReadOnlyList<TestResult> TestResults);

    // rung 4b-ii: an asker's record of a question it has sent but not yet seen answered.
    private sealed class Outstanding
    {
        public string Question;
        public DateTime LastSentAt;
        public int Attempts;
        public string CoordinatorWhenSent;   // who we last sent it to — a CHANGE means failover happened
        public Outstanding(string question, DateTime sentAt, int attempts, string coordinator)
        { Question = question; LastSentAt = sentAt; Attempts = attempts; CoordinatorWhenSent = coordinator; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static async Task RunAsync(int myPort, IReadOnlyList<int> peerPorts)
    {
        // The answer path (route/use/commission/compile/push/run, registry, git sync) is the
        // SHARED AgentCore — the same one the two-node agent uses. The swarm layer below adds only
        // coordination (election, quorum, heartbeats, assignment, in-flight recovery).
        AnthropicClient? client = AnthropicClient.FromEnvironment();
        var core = new AgentCore(client);
        core.LoadSharedHandlers();

        await using var node = new SwarmNode(myPort);
        var pending = new ConcurrentDictionary<string, string>(); // reqId -> origin asker (coordinator role: in-progress guard)
        int roundRobin = 0;

        // ── rung 4b-ii in-flight recovery state ───────────────────────────────────────────
        // doneAnswers: reqId -> finished answer. Every node keeps this for the reqs it coordinates,
        // so a duplicate ask/result for an already-answered reqId is served/dropped, not redone.
        var doneAnswers = new ConcurrentDictionary<string, (string Answer, string By)>();
        var doneOrder = new ConcurrentQueue<string>();   // FIFO of reqIds for bounded eviction
        const int DoneCap = 512;
        // outstanding: the ASKER's view of its own un-answered questions (for re-ask on failover).
        var outstanding = new ConcurrentDictionary<string, Outstanding>();
        // PRIMARY re-ask trigger = COORDINATOR CHANGE. The asker stamps each request with the
        // coordinator it sent to; when an election installs a different coordinator, the asker
        // immediately re-submits any still-outstanding request to the NEW one. That fires within
        // ~1s of the election completing (~seconds after a kill) — short enough to observe — instead
        // of waiting out a long timer. A 30s timeout is only a FALLBACK for a lost message with no
        // coordinator change. Re-asks can't double-generate: the handler marks the reqId in-flight
        // (see the "assign" case), so a re-ask to the new coordinator is dedup'd by `pending` while
        // work is in progress, and by `doneAnswers` once it has finished.
        const double ReAskTimeoutSeconds = 30.0;
        const int MaxAttempts = 4;                        // initial send + up to 3 re-asks, then give up

        // ── rung 5a fan-out (deliberation) state ──────────────────────────────────────────
        // The coordinator's open competitions, keyed by reqId. Each gathers competing candidates
        // until all expected arrive OR the collection window elapses (whichever first), then the
        // full slate is shown — no winner is picked (that's 5b).
        var competitions = new ConcurrentDictionary<string, Competition>();
        // Must comfortably exceed a candidate's generate-time. Candidates DON'T push (persist:false),
        // so they're faster than the answer path; 60s collects the typical slate while capping the
        // wait so one slow/dead node can't block the deliberation forever.
        const double CollectionWindowSeconds = 60.0;

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

        // ── The agent answer path — now the SHARED AgentCore. Stub if this node has no key. ──
        // A keyless node still participates in coordination; it just returns a routed stub so the
        // swarm's routing/election/recovery stay testable without an API key on every node.
        async Task<string> AnswerAsync(string question)
        {
            if (!core.HasLlm)
                return $"(node {node.Id} has no API key — routed stub; would answer: \"{question}\")";
            AnswerResult r = await core.AnswerAsync(question);
            return r.Text; // decline reply, capability answer, or generation-failure note — all deliverable
        }

        // ── Routing (rung 3), now targeting the ELECTED coordinator ──
        string PickHandler()
        {
            var members = LiveMembers().OrderBy(PortOf).ToList();
            int i = Interlocked.Increment(ref roundRobin);
            return members[((i % members.Count) + members.Count) % members.Count];
        }

        // Asker-side: a request is satisfied — stop tracking it (so recovery won't re-ask). Returns
        // true only the FIRST time, so a duplicate/late answer (e.g. original completion AND a
        // re-ask both delivering) is printed once and silently ignored thereafter.
        bool MarkAnswered(string reqId) => outstanding.TryRemove(reqId, out _);

        async Task DeliverAsync(string reqId, string answer, string answeredBy, string origin)
        {
            if (origin == node.Id) { if (MarkAnswered(reqId)) { Console.WriteLine($"\n[swarm answer — handled by {answeredBy}] {answer}"); Console.Write("> "); } }
            else await SendSwarm(origin, new SwarmMsg("deliver", reqId, Answer: answer, AnsweredBy: answeredBy, Coordinator: node.Id));
        }

        // Coordinator-side completion gate: the FIRST result for a reqId is recorded + delivered;
        // any later result for the same reqId (e.g. the original handler AND a re-assignment both
        // finished) is dropped. This is the dedup that neutralizes double-handling.
        async Task HandleResultAsync(string reqId, string answer, string answeredBy, string origin)
        {
            bool first = doneAnswers.TryAdd(reqId, (answer, answeredBy));
            pending.TryRemove(reqId, out _);
            if (!first) return; // duplicate completion — harmless, dropped
            doneOrder.Enqueue(reqId);
            while (doneOrder.Count > DoneCap && doneOrder.TryDequeue(out string? old)) doneAnswers.TryRemove(old, out _);
            await DeliverAsync(reqId, answer, answeredBy, origin);
        }

        // A finished assignment goes home through the coordinator (which records it for dedup)
        // when one is reachable — but DIRECT to the asker when no coordinator is reachable yet, so
        // the answer isn't lost during the election gap right after the assigning coordinator died.
        async Task CompleteAssignAsync(string reqId, string answer, string asker)
        {
            string coordNow; lock (stateLock) coordNow = coordinator;
            if (coordNow == node.Id) { await HandleResultAsync(reqId, answer, node.Id, asker); return; }
            if (node.Peers.Contains(coordNow)) { await SendSwarm(coordNow, new SwarmMsg("result", reqId, Answer: answer, AnsweredBy: node.Id, Origin: asker)); return; }
            // No coordinator reachable (it died and the election isn't finished) — don't lose the
            // answer: deliver it straight to the asker.
            Console.WriteLine($"[recovery] coordinator unreachable — delivering req {reqId} direct to asker {asker}");
            await DeliverAsync(reqId, answer, node.Id, asker);
        }

        async Task CoordinateAsync(string reqId, string question, string origin)
        {
            // Already answered (a re-ask that raced a completion)? Serve the cached answer, no work.
            if (doneAnswers.TryGetValue(reqId, out var cached)) { await DeliverAsync(reqId, cached.Answer, cached.By, origin); return; }
            // Already being worked here? Ignore the duplicate (re-ask to the SAME coordinator).
            if (!pending.TryAdd(reqId, origin)) return;

            string handler = PickHandler();
            Console.WriteLine($"[coordinator] assigning req {reqId} to {handler}");
            // assign carries the origin so a handler can deliver home even if THIS coordinator later dies.
            if (handler == node.Id) { string a = await AnswerAsync(question); await HandleResultAsync(reqId, a, node.Id, origin); }
            else await SendSwarm(handler, new SwarmMsg("assign", reqId, Question: question, Origin: origin));
        }

        // (Re)send an ask to whoever is coordinator right now. Used for the first send and re-asks.
        async Task DispatchAskAsync(string reqId, string question)
        {
            string c; lock (stateLock) c = coordinator;
            if (c == node.Id) await CoordinateAsync(reqId, question, node.Id);
            else if (node.Peers.Contains(c)) await SendSwarm(c, new SwarmMsg("ask", reqId, Question: question, Origin: node.Id));
            else Console.WriteLine($"[ask] coordinator {c} not reachable yet — recovery will retry.");
        }

        async Task AskSwarmAsync(string question)
        {
            string reqId = Guid.NewGuid().ToString("N")[..8];
            string c; lock (stateLock) c = coordinator;
            // Track BEFORE sending, stamped with the coordinator we're sending to — so recovery can
            // detect a later coordinator CHANGE (failover) and re-drive this request. Survives the
            // coordinator's death because it lives here, on the asker.
            outstanding[reqId] = new Outstanding(question, DateTime.UtcNow, 1, c);
            Console.WriteLine($"[ask] req {reqId} → coordinator {c}");
            await DispatchAskAsync(reqId, question);
        }

        // ── rung 5a: fan-out and collect (DELIBERATION) ──────────────────────────────────
        // Kept ALONGSIDE assign-to-one (above): `<question>` still assigns to one node; the new
        // `deliberate <question>` fans out so every node generates its OWN candidate. Keeping both
        // means rungs 1–4b-ii (which all run through assign-to-one) are completely untouched.

        // Add a collected candidate; finalize early once every expected member has reported.
        void AddCandidate(string reqId, string member, string status, string? cap, string? desc, string source, string answer, IReadOnlyList<TestResult> results)
        {
            bool finalizeNow = false;
            if (competitions.TryGetValue(reqId, out Competition? comp))
            {
                lock (comp)
                {
                    if (comp.Finalized) return; // window already closed — a late straggler, ignore
                    comp.Collected.Add(new CollectedCandidate(member, status, cap, desc, source, answer, results));
                    int passed = results.Count(r => r.Pass);
                    Console.WriteLine($"[deliberate] req {reqId}: candidate {comp.Collected.Count}/{comp.Expected} from {member} ({status}, tests {passed}/{results.Count})");
                    Console.Write("> ");
                    if (comp.Collected.Count >= comp.Expected) finalizeNow = true;
                }
            }
            if (finalizeNow) FinalizeCompetition(reqId, "all candidates in");
        }

        // Close a competition exactly once (rung 5b): show the slate, SCORE it, pick the winner,
        // push ONLY the winner if it clears the quality floor, and deliver the winning answer.
        void FinalizeCompetition(string reqId, string why)
        {
            if (!competitions.TryGetValue(reqId, out Competition? comp)) return;
            List<CollectedCandidate> slate;
            lock (comp)
            {
                if (comp.Finalized) return;
                comp.Finalized = true;
                slate = comp.Collected.ToList();
            }
            competitions.TryRemove(reqId, out _);

            // ── slate (5a display, kept for visibility) ──
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[deliberation {reqId}] \"{comp.Question}\" [{CapTypes.Name(comp.InputType)}→{CapTypes.Name(comp.OutputType)}] — collected {slate.Count}/{comp.Expected} candidate(s) ({why}):");
            for (int i = 0; i < slate.Count; i++)
            {
                CollectedCandidate c = slate[i];
                int p = c.TestResults.Count(r => r.Pass);
                string src = c.Source.Length > 0 ? $", src {c.Source.Length} chars" : "";
                sb.AppendLine($"  {i + 1}. {c.Member} via '{c.Capability ?? "—"}' [{c.Status}] — tests {p}/{c.TestResults.Count}{src} — answer: {c.Answer}");
            }

            // Possibly-bad-test signal (fallible LLM tests): a test EVERY Ok candidate failed is
            // suspect — surface it, but don't act on it (the majority floor already tolerates it).
            var okForTests = slate.Where(c => c.Status == "Ok" && c.TestResults.Count > 0).ToList();
            if (okForTests.Count > 1)
                foreach (var grp in okForTests.SelectMany(c => c.TestResults).GroupBy(r => r.Input))
                    if (grp.All(r => !r.Pass))
                        sb.AppendLine($"  note: every candidate failed test \"{grp.Key}\" (expected \"{grp.First().Expected}\") — the test may be wrong.");

            // ── SCORE + SELECT ──
            // Primary: most tests passed. Disqualify non-Ok (GenerationFailed/Declined). Tie-break:
            // shortest source (parsimony — simpler code), then lowest port (deterministic, the
            // swarm's canonical ordering) so the SAME slate always yields the SAME winner.
            CollectedCandidate? winner = slate
                .Where(c => c.Status == "Ok")
                .OrderByDescending(c => c.TestResults.Count(r => r.Pass))
                .ThenBy(c => c.Source.Length)
                .ThenBy(c => PortOf(c.Member))
                .FirstOrDefault();

            string outcome;
            if (winner is null)
            {
                outcome = "no node produced a working implementation — nothing adopted.";
            }
            else
            {
                int passed = winner.TestResults.Count(r => r.Pass);
                int total = winner.TestResults.Count;
                // Quality floor (shared with composition's link validation): must pass a MAJORITY of
                // tests to be eligible to propagate. Strict "all" is too brittle given fallible LLM
                // tests (one bad test would block a good handler); a majority tolerates one wrong
                // test yet still demands broad correctness.
                bool clearsFloor = AgentCore.ClearsQualityFloor(passed, total);

                if (clearsFloor && winner.Source.Length > 0)
                {
                    Console.WriteLine($"[deliberation {reqId}] winner: {winner.Member} ({passed}/{total}) — pushing '{winner.Capability}' [{CapTypes.Name(comp.InputType)}→{CapTypes.Name(comp.OutputType)}] to the swarm.");
                    bool pushed = core.TryPersistWinner(winner.Capability ?? "capability", winner.Description ?? comp.Question, comp.Question, winner.Source, comp.InputType, comp.OutputType);
                    outcome = pushed
                        ? $"WINNER {winner.Member} via '{winner.Capability}' ({passed}/{total} tests) — ADOPTED + propagated to the swarm.\n  answer: {winner.Answer}"
                        : $"WINNER {winner.Member} via '{winner.Capability}' ({passed}/{total} tests) — selected (push unavailable here).\n  answer: {winner.Answer}";
                }
                else if (clearsFloor) // winner reused an already-shared handler — already canonical
                {
                    outcome = $"WINNER {winner.Member} via '{winner.Capability}' ({passed}/{total} tests) — already the shared handler.\n  answer: {winner.Answer}";
                }
                else // best-available answer to the asker, but too weak to become canonical
                {
                    outcome = $"BEST-AVAILABLE from {winner.Member} ({passed}/{total} tests) — below the majority floor, so NOT adopted (no handler propagated).\n  answer: {winner.Answer}";
                }
            }

            sb.AppendLine(outcome);
            string text = sb.ToString().TrimEnd();
            Console.WriteLine("\n" + text);
            Console.Write("> ");
            if (comp.Origin != node.Id) _ = SendSwarm(comp.Origin, new SwarmMsg("slate", reqId, Answer: text));
        }

        // The coordinator's own candidate (it's a member too), produced concurrently with the peers'.
        async Task ProduceOwnCandidateAsync(string reqId, string question, CapType inType, CapType outType, IReadOnlyList<TestCase> tests)
        {
            if (!core.HasLlm)
            { AddCandidate(reqId, node.Id, "Declined", null, null, "", $"(node {node.Id} has no API key)", Array.Empty<TestResult>()); return; }
            Candidate cand = await core.ProduceCandidateAsync(question, inType, outType, tests);
            AddCandidate(reqId, node.Id, cand.Status.ToString(), cand.Capability, cand.Description, cand.Source, cand.Answer, cand.TestResults);
        }

        // Close the competition when the collection window elapses (whatever arrived, we proceed).
        async Task FinalizeAfterWindowAsync(string reqId)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(CollectionWindowSeconds), loopCts.Token); } catch { return; }
            FinalizeCompetition(reqId, $"collection window {CollectionWindowSeconds:0}s elapsed");
        }

        // Coordinator side of a deliberation: generate test cases, broadcast the question to every
        // member as a candidate-request, gather candidates within the window, then show the slate.
        async Task RunCompetitionAsync(string reqId, string question, string origin)
        {
            Console.WriteLine($"\n[deliberate] req {reqId}: fanning \"{question}\" out to the swarm");
            Console.Write("> ");
            // ONE LLM call: infer the declared input/output types + generate type-consistent tests.
            DeliberationSpec spec = await core.PrepareDeliberationAsync(question);
            string testsJson = JsonSerializer.Serialize(spec.Tests);
            Console.WriteLine($"[deliberate] req {reqId}: types {CapTypes.Name(spec.InputType)}→{CapTypes.Name(spec.OutputType)}, {spec.Tests.Count} test case(s)");
            Console.Write("> ");

            var members = LiveMembers();
            competitions[reqId] = new Competition(origin, question, members.Count, spec.InputType, spec.OutputType);

            // Fan out to peers (all targeting the SAME declared types); coordinator competes too.
            foreach (string peer in members.Where(m => m != node.Id))
                await SendSwarm(peer, new SwarmMsg("candreq", reqId, Question: question, Origin: origin,
                    TestCases: testsJson, InType: CapTypes.Name(spec.InputType), OutType: CapTypes.Name(spec.OutputType)));
            _ = ProduceOwnCandidateAsync(reqId, question, spec.InputType, spec.OutputType, spec.Tests);
            _ = FinalizeAfterWindowAsync(reqId);
            // >>> rung 4b-ii-style recovery would attach HERE: if the coordinator dies mid-collection
            //     the asker could re-issue `deliberate` to the new coordinator. For 5a this in-flight
            //     fan-out is simply lost on coordinator death (acceptable, by spec). <<<
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
                {
                    string asker = m.Origin ?? from;
                    // Mark this reqId in-flight ON THIS NODE too. If the coordinator that assigned it
                    // dies and I (the handler) get elected coordinator, an incoming re-ask for the
                    // SAME reqId is then dedup'd by this `pending` entry instead of being dispatched
                    // again — that's what stops a re-ask from causing a SECOND commissioning.
                    pending[m.ReqId] = asker;
                    Console.WriteLine($"\n[assigned by {from}] handling: \"{m.Question}\"");
                    string ans = await AnswerAsync(m.Question ?? "");
                    // Deliver failover-aware: through the current coordinator if reachable, else
                    // straight to the asker (the assigning coordinator may have died mid-work).
                    await CompleteAssignAsync(m.ReqId, ans, asker);
                    Console.Write("> ");
                    break;
                }
                case "result":
                {
                    string asker = m.Origin ?? (pending.TryGetValue(m.ReqId, out string? po) ? po : node.Id);
                    await HandleResultAsync(m.ReqId, m.Answer ?? "", m.AnsweredBy ?? from, asker);
                    break;
                }
                case "deliver":
                    // Print only the first delivery for this reqId (the original completion and a
                    // re-ask can both arrive); a later duplicate is dropped silently.
                    if (MarkAnswered(m.ReqId))
                    {
                        Console.WriteLine($"\n[swarm answer ← coordinator {m.Coordinator}, handled by {m.AnsweredBy}] {m.Answer}");
                        Console.Write("> ");
                    }
                    break;

                // ── rung 5a fan-out ──
                case "compete": // asker → coordinator: run a deliberation for this question
                    await RunCompetitionAsync(m.ReqId, m.Question ?? "", m.Origin ?? from);
                    break;
                case "candreq": // coordinator → member: produce your own candidate + test results
                {
                    Console.WriteLine($"\n[candidate] {from} asked me to compete on: \"{m.Question}\"");
                    Console.Write("> ");
                    IReadOnlyList<TestCase> tests;
                    try { tests = JsonSerializer.Deserialize<List<TestCase>>(m.TestCases ?? "[]", JsonOpts) ?? new(); }
                    catch { tests = Array.Empty<TestCase>(); }
                    CapType inT = CapTypes.Parse(m.InType), outT = CapTypes.Parse(m.OutType);   // coordinator-fixed types
                    string status, answer, source; string? cap, desc; IReadOnlyList<TestResult> results;
                    if (!core.HasLlm)
                    { status = "Declined"; cap = null; desc = null; source = ""; answer = $"(node {node.Id} has no API key)"; results = Array.Empty<TestResult>(); }
                    else
                    {
                        Candidate cand = await core.ProduceCandidateAsync(m.Question ?? "", inT, outT, tests);
                        status = cand.Status.ToString(); cap = cand.Capability; desc = cand.Description; source = cand.Source; answer = cand.Answer; results = cand.TestResults;
                    }
                    await SendSwarm(from, new SwarmMsg("candidate", m.ReqId, Answer: answer, AnsweredBy: node.Id,
                        CapName: cap, CapDesc: desc, Source: source, Status: status, TestResults: JsonSerializer.Serialize(results), Origin: m.Origin));
                    Console.Write("> ");
                    break;
                }
                case "candidate": // member → coordinator: a competing candidate has arrived
                {
                    IReadOnlyList<TestResult> results;
                    try { results = JsonSerializer.Deserialize<List<TestResult>>(m.TestResults ?? "[]", JsonOpts) ?? new(); }
                    catch { results = Array.Empty<TestResult>(); }
                    AddCandidate(m.ReqId, m.AnsweredBy ?? from, m.Status ?? "Ok", m.CapName, m.CapDesc, m.Source ?? "", m.Answer ?? "", results);
                    break;
                }
                case "slate": // coordinator → asker: the full collected slate (no winner yet)
                    Console.WriteLine("\n" + (m.Answer ?? "(empty slate)"));
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

        // ── rung 4b-ii: asker-side recovery. Re-drive an outstanding request when EITHER the
        // coordinator changed since we sent it (an election happened → the node that held our
        // request likely died) OR a fallback timeout elapsed (a lost message with no election).
        // The coordinator-change trigger is what makes recovery prompt: it fires ~1s after the new
        // coordinator is installed, so the answer comes home seconds after a kill, not minutes.
        async Task AskerRecoveryLoop()
        {
            while (!loopCts.IsCancellationRequested)
            {
                try { await Task.Delay(1000, loopCts.Token); } catch { break; }
                string c; lock (stateLock) c = coordinator;
                foreach (var kv in outstanding.ToArray())
                {
                    Outstanding o = kv.Value; string reqId = kv.Key;
                    bool coordChanged = c != o.CoordinatorWhenSent;
                    bool timedOut = (DateTime.UtcNow - o.LastSentAt).TotalSeconds > ReAskTimeoutSeconds;
                    if (!coordChanged && !timedOut) continue;
                    if (o.Attempts >= MaxAttempts)
                    {
                        if (outstanding.TryRemove(reqId, out _))
                        { Console.WriteLine($"\n[recovery] giving up on req {reqId} after {o.Attempts} attempts — unanswerable."); Console.Write("> "); }
                        continue;
                    }
                    o.Attempts++; o.LastSentAt = DateTime.UtcNow; o.CoordinatorWhenSent = c;
                    string why = coordChanged ? "coordinator changed (failover)" : $"no answer in {ReAskTimeoutSeconds:0}s";
                    Console.WriteLine($"\n[recovery] re-asking req {reqId} — {why} — to coordinator {c} (attempt {o.Attempts}/{MaxAttempts}).");
                    Console.Write("> ");
                    await DispatchAskAsync(reqId, o.Question);
                }
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
        _ = AskerRecoveryLoop();

        Console.WriteLine();
        Console.WriteLine($"Swarm-agent {node.Id}." + (client is null ? " (no API key — answers with stubs.)" : ""));
        Console.WriteLine("Commands:  <question>   ask the swarm (assign-to-one)   |   deliberate <question>  fan-out: every node competes");
        Console.WriteLine("           compose <question>  chain existing typed capabilities   |   @<port> <msg>  direct");
        Console.WriteLine("           peers   |   coordinator   |   pause <secs>   |   exit");
        Console.WriteLine();

        while (true)
        {
            Console.Write("> ");
            string? raw = Console.ReadLine();
            if (raw is null) break;
            // Strip a leading UTF-8 BOM (U+FEFF) — piped stdin can prepend one, and .NET's
            // Trim() does NOT treat it as whitespace, which would otherwise break command matching.
            string line = raw.Trim().TrimStart('﻿').Trim();
            if (line.Equals("exit", StringComparison.OrdinalIgnoreCase)) break;
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
            if (line.StartsWith("compose ", StringComparison.OrdinalIgnoreCase))
            {
                // Composition is a LOCAL registry operation: every node shares the catalog (pulled
                // from GitHub), so this node can decompose + chain its own capabilities. No fan-out.
                string q = line[("compose ".Length)..].Trim();
                if (q.Length == 0) { Console.WriteLine("usage: compose <question>"); continue; }
                if (!core.HasLlm) { Console.WriteLine("[compose] this node has no API key — can't decompose."); continue; }
                CompositionResult cr = await core.ComposeAsync(q);
                switch (cr.Kind)
                {
                    case CompositionKind.Chain:
                        Console.WriteLine($"[composition] answer (via {string.Join(" → ", cr.Steps)}): {cr.Text}");
                        break;
                    case CompositionKind.NotComposite:
                        Console.WriteLine($"[composition] not a composite — single capability handled it: {cr.Text}");
                        break;
                    case CompositionKind.Failed:
                        Console.WriteLine($"[composition] {cr.Text}");
                        break;
                }
                continue;
            }
            if (line.StartsWith("deliberate ", StringComparison.OrdinalIgnoreCase))
            {
                string q = line[("deliberate ".Length)..].Trim();
                if (q.Length == 0) { Console.WriteLine("usage: deliberate <question>"); continue; }
                // Route the deliberation through the elected coordinator (it orchestrates the fan-out).
                string reqId = Guid.NewGuid().ToString("N")[..8];
                string c; lock (stateLock) c = coordinator;
                Console.WriteLine($"[deliberate] req {reqId} → coordinator {c}");
                if (c == node.Id) await RunCompetitionAsync(reqId, q, node.Id);
                else if (node.Peers.Contains(c)) await SendSwarm(c, new SwarmMsg("compete", reqId, Question: q, Origin: node.Id));
                else Console.WriteLine($"[deliberate] coordinator {c} not reachable yet — try again in a moment.");
                continue;
            }
            await AskSwarmAsync(line);
        }

        loopCts.Cancel();
        Console.WriteLine("Goodbye.");
    }
}
