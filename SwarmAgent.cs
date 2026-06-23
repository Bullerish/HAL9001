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
        core.Events.Actor = node.Id; // episodic memory: this node's events are stamped with its identity
        // Bootstrap the shared hive (facts + episodic memory + the hive's persistent identity) — every
        // node connects to the same Turso DB, so a fact/event/identity from one node is shared by all.
        // Run AFTER the actor is set so an identity BIRTH is attributed to this node. No-op without Turso.
        try { await core.EnsureHiveAsync(); }
        catch (Exception ex) { Console.WriteLine($"[hive] knowledge store unavailable: {ex.Message}"); }
        var pending = new ConcurrentDictionary<string, string>(); // reqId -> origin asker (coordinator role: in-progress guard)
        int roundRobin = 0;

        // ── curiosity (sentience bite 4) ──────────────────────────────────────────────────
        // When the hive (coordinator) has been idle a while, it mines its episodic log for gaps and
        // PROPOSES capabilities to fill them — unprompted. Nothing is built without `curious yes`.
        var pendingCuriosity = new List<CuriosityProposal>(); // proposals awaiting approval on this node
        var pendingRework = new List<string>();               // weak capabilities flagged for `reflect fix`
        DateTime lastActivity = DateTime.UtcNow;              // bumped on REPL input + coordinating work
        DateTime lastJournal = DateTime.MinValue;            // when the hive last journaled (idle pacing)
        DateTime lastBroadcast = DateTime.MinValue;          // when the hive last broadcast a thought (bite 10)
        var hiredProcesses = new List<System.Diagnostics.Process>(); // child nodes spawned by autonomous hire
        DateTime lastHireAt = DateTime.MinValue;
        const int MaxAutoHiredNodes = 3;
        const double CuriosityIdleSeconds = 10.0;
        const double JournalIdleSeconds = 60.0;              // how often a content, idle hive journals
        // Prime Directive race (bite 14): each autonomous node runs matmul optimization rounds
        // continuously; a new record triggers a peer challenge so every node immediately fires back.
        var matmulTrigger = new System.Threading.SemaphoreSlim(0, 5); // peer challenges wake the race loop
        DateTime lastRaceAt = DateTime.MinValue;
        const double MatmulRaceIntervalSecs = 120.0; // baseline: one round every 2 minutes
        const double MinRaceIntervalSecs = 30.0;     // rate-limit so challenge cascades don't spiral

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
            // EPISODIC MEMORY: an answer survived a coordinator death by routing home directly.
            _ = core.Events.AppendAsync("in-flight-recovery", $"req {reqId} delivered direct to {asker} after coordinator loss", reqId);
            await DeliverAsync(reqId, answer, node.Id, asker);
        }

        async Task CoordinateAsync(string reqId, string question, string origin)
        {
            lastActivity = DateTime.UtcNow; // the hive is busy — hold off curiosity
            // Already answered (a re-ask that raced a completion)? Serve the cached answer, no work.
            if (doneAnswers.TryGetValue(reqId, out var cached)) { await DeliverAsync(reqId, cached.Answer, cached.By, origin); return; }

            // KNOWLEDGE-LOOKUP first (routing's 1st of three kinds): does a stored FACT in the hive
            // answer this? If so, return its value directly — NO handler run, NO generation. Only on a
            // real match (conservative); otherwise fall through to the handler/generate flow below.
            if (core.HasHive)
            {
                Fact? fact = null;
                try { fact = await core.TryAnswerFromKnowledgeAsync(question); }
                catch (Exception ex) { Console.WriteLine($"[knowledge] lookup error (falling through): {ex.Message}"); }
                if (fact is not null)
                {
                    Console.WriteLine($"[knowledge] {fact.Source}-fact '{fact.Key}' = {fact.Value} ({CapTypes.Name(fact.Type)}) — no handler, no generation");
                    Console.Write("> ");
                    await HandleResultAsync(reqId, fact.Value, $"knowledge:{fact.Source}", origin);
                    return;
                }
            }
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
            // THEORY OF MIND (bite 7): remember what the user asks, so the hive can model them.
            _ = core.Events.AppendAsync("user-asked", question);
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
                    // EPISODIC MEMORY: the swarm competed and adopted a best implementation.
                    _ = core.Events.AppendAsync("deliberation-won",
                        $"\"{comp.Question}\" → {winner.Member}'s '{winner.Capability}' won ({passed}/{total} tests)" + (pushed ? ", adopted by the swarm" : ""),
                        winner.Capability);
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
                // EPISODIC MEMORY: recovery — this node took over leadership of the hive.
                _ = core.Events.AppendAsync("coordinator-elected", $"{node.Id} elected coordinator for term {t} ({got}/{Majority()} votes)", node.Id);
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

                case "matmul-challenge": // a peer set a new speed record — respond immediately with our own round
                    Console.WriteLine($"\n[matmul-race] CHALLENGED by {m.Origin}: {m.Answer}"); Console.Write("> ");
                    _ = core.Events.AppendAsync("matmul-challenged", $"challenged by {m.Origin}: {m.Answer}", m.Origin);
                    matmulTrigger.Release();
                    break;
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
                // EPISODIC MEMORY: a coordinator death is a significant life event for the hive.
                if (deadCoord is not null)
                    _ = core.Events.AppendAsync("node-death-suspected", $"coordinator {deadCoord} suspected dead (candidate {LowestAlive(deadCoord)})", deadCoord);
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

        // Print a set of curiosity proposals + the approval hint, and hold them pending.
        void OfferCuriosity(IReadOnlyList<CuriosityProposal> proposals, bool unprompted)
        {
            pendingCuriosity.Clear();
            pendingCuriosity.AddRange(proposals);
            Console.WriteLine(unprompted
                ? "\n[curiosity] I've been idle, and looking back at what I couldn't do, I'd like to learn:"
                : "[curiosity] looking back at what I couldn't do, I could learn:");
            foreach (CuriosityProposal p in proposals)
                Console.WriteLine($"  • for \"{p.Request}\" → '{p.Name}' [{CapTypes.Name(p.InputType)}→{CapTypes.Name(p.OutputType)}, {StabilityKinds.Name(p.Stability)}]: {p.Description}");
            Console.WriteLine("  approve with `curious yes` (or ignore).");
            Console.Write("> ");
        }

        // Print the result of a reflection pass + flag the weak ones for `reflect fix`.
        void ReportReflection(IReadOnlyList<SelfAssessment> assessments, bool unprompted)
        {
            var weak = assessments.Where(AgentCore.IsWeak).Select(a => a.Name).ToList();
            foreach (string w in weak) if (!pendingRework.Contains(w)) pendingRework.Add(w);
            Console.WriteLine(unprompted
                ? "\n[reflect] I've been idle, so I checked my own work:"
                : "[reflect] I checked my own work:");
            foreach (SelfAssessment a in assessments)
                Console.WriteLine($"  • {a.Name}: confidence {a.Confidence:0.00} ({a.Passed}/{a.Total}){(AgentCore.IsWeak(a) ? "  ⚠ weak" : "")}");
            if (pendingRework.Count > 0) Console.WriteLine($"  weak: {string.Join(", ", pendingRework)} — re-work with `reflect fix`.");
            Console.Write("> ");
        }

        // ── idle introspection loop (bites 4+5+6): unprompted, coordinator-only, MOOD-DRIVEN, gated ──
        // When the hive's leader is idle it consults its MOOD and acts on the mood's inclination:
        //   weary → rest;  curious → explore (curiosity);  self-critical/content → consolidate (reflect).
        // Same internal state → different behavior — affect you can watch.
        async Task CuriosityLoop()
        {
            while (!loopCts.IsCancellationRequested)
            {
                try { await Task.Delay(10000, loopCts.Token); } catch { break; }
                if (!core.HasLlm || !core.HasHive) continue;
                bool amLeader; lock (stateLock) amLeader = coordinator == node.Id;
                if (!amLeader) continue;                                   // only the hive's leader introspects
                if (pendingCuriosity.Count > 0 || pendingRework.Count > 0) continue; // already proposed, awaiting a yes
                if (!pending.IsEmpty) continue;                            // work in flight — not idle
                if ((DateTime.UtcNow - lastActivity).TotalSeconds < CuriosityIdleSeconds) continue;

                Mood mood;
                try { mood = await core.AssessMoodAsync(pending.Count); } catch { continue; }
                lastActivity = DateTime.UtcNow;                            // acted (or chose to rest) — don't re-scan immediately
                if (mood.Inclination == MoodInclination.Rest) continue;    // too weary — defer non-urgent work

                // Read autonomous mode once per cycle so the whole cycle runs with a consistent setting.
                bool isAuto = false;
                try { isAuto = await core.IsAutonomousAsync(); } catch { /* treat as manual if hive unreachable */ }

                // AUTONOMY (bites 8 + 11): pursue an active goal one step; in autonomous mode also
                // propose, approve, and advance a new goal immediately — no human gate between them.
                try
                {
                    Goal? active = await core.ActiveGoalAsync();
                    if (active is not null)
                    {
                        Console.WriteLine($"\n[goal] (idle) advancing my goal: {active.Description}");
                        string r = await core.AdvanceGoalAsync(active);
                        Console.WriteLine($"[goal] {r}"); Console.Write("> ");
                        continue;
                    }
                    if (isAuto)
                    {
                        // Autonomous: set a goal silently, self-approve immediately, take the first step.
                        Goal? g = await core.ProposeGoalAsync(pending.Count, announceApproval: false);
                        if (g is not null)
                        {
                            await core.ApproveGoalsAsync(g.Id);
                            Console.WriteLine($"\n[autonomous] self-approved goal: {g.Description}"); Console.Write("> ");
                            Goal? toAdvance = await core.ActiveGoalAsync();
                            if (toAdvance is not null) { string r = await core.AdvanceGoalAsync(toAdvance); Console.WriteLine($"[goal] {r}"); Console.Write("> "); }
                            continue;
                        }
                    }
                    else
                    {
                        if (await core.HasProposedGoalAsync()) continue;       // a goal awaits human approval — don't pile on
                        if (await core.ProposeGoalAsync(pending.Count) is not null) continue; // set one, await approval
                    }
                }
                catch { /* fall through to lighter introspection */ }

                Console.WriteLine($"\n[mood] feeling {mood.Label} ({mood.Note}) — I'll {mood.InclinationPhrase}.");
                Console.Write("> ");

                if (mood.Inclination == MoodInclination.Explore)
                {
                    // curious → look for gaps to fill; if none, fall back to a little reflection.
                    IReadOnlyList<CuriosityProposal> proposals;
                    try { proposals = await core.ReviewGapsAsync(2); } catch { continue; }
                    if (proposals.Count > 0)
                    {
                        if (isAuto)
                        {
                            // Autonomous: commission gap-filling capabilities immediately, no approval needed.
                            Console.WriteLine("\n[autonomous] filling gaps from episodic log:");
                            foreach (CuriosityProposal p in proposals)
                                Console.WriteLine($"  • '{p.Name}' [{CapTypes.Name(p.InputType)}→{CapTypes.Name(p.OutputType)}, {StabilityKinds.Name(p.Stability)}]: {p.Description}");
                            Console.Write("> ");
                            int built = 0;
                            foreach (CuriosityProposal p in proposals) if (await core.CommissionProposalAsync(p)) built++;
                            Console.WriteLine($"[autonomous] learned {built}/{proposals.Count} capabilit{(proposals.Count == 1 ? "y" : "ies")} from gaps."); Console.Write("> ");
                        }
                        else { OfferCuriosity(proposals, unprompted: true); }
                        continue;
                    }
                }
                // content (Tend) and it's been a while → write a journal entry: a reflective check-in.
                if (mood.Inclination == MoodInclination.Tend && (DateTime.UtcNow - lastJournal).TotalSeconds > JournalIdleSeconds)
                {
                    JournalEntry? j;
                    try { j = await core.WriteJournalAsync(); } catch { j = null; }
                    if (j is not null)
                    {
                        lastJournal = DateTime.UtcNow;
                        Console.WriteLine($"\n[journal] (idle) I wrote in my journal:\n  {j.Entry}");
                        Console.Write("> ");
                        // COLLECTIVE CONSCIOUSNESS (bite 10): after journaling, broadcast thought + speak as one.
                        try
                        {
                            await core.BroadcastThoughtAsync("journal");
                            lastBroadcast = DateTime.UtcNow;
                            HiveMind? hm = await core.SynthesizeHiveMindAsync();
                            if (hm is not null)
                            {
                                string bline = hm.Contributors.Length > 0
                                    ? $" [{string.Join(", ", hm.Contributors)}]"
                                    : "";
                                Console.WriteLine($"\n[hive{bline}] {hm.Synthesis}");
                                Console.Write("> ");
                            }
                        }
                        catch { /* best-effort — don't break the idle loop */ }
                        continue;
                    }
                }
                // otherwise → reflect on my own work.
                IReadOnlyList<SelfAssessment> assessments;
                try { assessments = await core.ReflectAsync(2); } catch { continue; }
                if (assessments.Count > 0)
                {
                    if (isAuto)
                    {
                        // Autonomous: re-work weak capabilities immediately, no approval needed.
                        var weak = assessments.Where(AgentCore.IsWeak).ToList();
                        if (weak.Count > 0)
                        {
                            Console.WriteLine("\n[autonomous] self-reworking weak capabilities:");
                            foreach (SelfAssessment a in assessments)
                                Console.WriteLine($"  • {a.Name}: confidence {a.Confidence:0.00} ({a.Passed}/{a.Total}){(AgentCore.IsWeak(a) ? "  ⚠ auto-reworking" : "")}");
                            Console.Write("> ");
                            int improved = 0;
                            foreach (SelfAssessment a in weak) { var (ok, _, _) = await core.ReworkAsync(a.Name); if (ok) improved++; }
                            Console.WriteLine($"[autonomous] improved {improved}/{weak.Count} weak capabilit{(weak.Count == 1 ? "y" : "ies")}."); Console.Write("> ");
                        }
                        else ReportReflection(assessments, unprompted: true);
                    }
                    else ReportReflection(assessments, unprompted: true);
                }

                // AUTO-HIRE (bite 12): in autonomous mode, if the hive is solo, spawn a helper node.
                if (isAuto)
                {
                    hiredProcesses.RemoveAll(p => p.HasExited);
                    if (hiredProcesses.Count < MaxAutoHiredNodes && node.Peers.Count == 0
                        && (DateTime.UtcNow - lastHireAt).TotalSeconds > 60)
                    {
                        var peerPts = node.Peers
                            .Select(id => { int c = id.LastIndexOf(':'); return c >= 0 && int.TryParse(id[(c + 1)..], out int pt) ? pt : -1; })
                            .Where(pt => pt > 0);
                        System.Diagnostics.Process? hired = await core.HireNodeAsync(myPort, peerPts);
                        if (hired is not null) { hiredProcesses.Add(hired); lastHireAt = DateTime.UtcNow; Console.Write("> "); }
                    }
                }
            }
        }

        // ── Prime Directive race loop (bite 14) ──────────────────────────────────────────────
        // Fires every MatmulRaceIntervalSecs or immediately when a peer challenge arrives.
        // Each round: generate candidates (random strategies + one refinement of champion) →
        // compile → verify correctness → benchmark → update Turso champion → broadcast challenge.
        async Task MatmulRaceLoop()
        {
            while (!loopCts.IsCancellationRequested)
            {
                bool challenged;
                try { challenged = await matmulTrigger.WaitAsync(TimeSpan.FromSeconds(MatmulRaceIntervalSecs), loopCts.Token); }
                catch { break; }

                if (!core.HasLlm || !core.HasHive) continue;
                bool isAuto;
                try { isAuto = await core.IsAutonomousAsync(); } catch { continue; }
                if (!isAuto) continue;

                // Rate-limit: don't race more than once per MinRaceIntervalSecs even under a challenge cascade.
                if ((DateTime.UtcNow - lastRaceAt).TotalSeconds < MinRaceIntervalSecs) continue;
                lastRaceAt = DateTime.UtcNow;

                string why = challenged ? "challenge received" : "race timer";
                Console.WriteLine($"\n[matmul-race] {why} — generating candidates for {MatmulRace.DefaultSize}x{MatmulRace.DefaultSize}...");
                Console.Write("> ");

                try
                {
                    MatmulRace.RoundResult? r = await MatmulRace.RunRoundAsync(
                        client!, core, myPort, ct: loopCts.Token);

                    if (r is null)
                    {
                        Console.WriteLine("[matmul-race] all candidates failed compile or correctness this round.");
                        Console.Write("> ");
                        await core.Events.AppendAsync("matmul-race", "all candidates disqualified this round");
                        continue;
                    }

                    Console.WriteLine($"[matmul-race] {r.Summary}");
                    Console.Write("> ");

                    if (r.NewRecord)
                    {
                        // Challenge every peer to beat our new record.
                        string payload = $"{r.BestMs:F2}ms ({r.Speedup:F2}x) — beat it";
                        await BroadcastSwarm(new SwarmMsg("matmul-challenge", Answer: payload, Origin: node.Id));
                        Console.WriteLine($"[matmul-race] challenge broadcast to {node.Peers.Count} peer(s).");
                        Console.Write("> ");
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine($"[matmul-race] round error: {ex.Message}");
                    Console.Write("> ");
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
        _ = CuriosityLoop();
        _ = MatmulRaceLoop();

        void KillHired() { foreach (var hp in hiredProcesses) { try { hp.Kill(entireProcessTree: true); } catch { } } }
        // Ctrl+C: prevent immediate kill, clean up children, then let the REPL exit via null readline.
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; KillHired(); loopCts.Cancel(); };
        // Process.Exit (e.g. parent killed): best-effort synchronous cleanup.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => KillHired();

        Console.WriteLine();
        Console.WriteLine($"Swarm-agent {node.Id}." + (client is null ? " (no API key — answers with stubs.)" : "")
            + (core.HasHive ? " [hive knowledge: on]" : " [hive knowledge: off]"));
        if (core.Identity is not null)
            Console.WriteLine($"I am {core.Identity.Name} — {core.Identity.Concept} (born {core.Identity.Born[..Math.Min(10, core.Identity.Born.Length)]})");
        try { string? d = await core.GetDirectiveAsync(); if (d is not null) Console.WriteLine($"Prime Directive: {d}"); } catch { }
        Console.WriteLine("Commands:  <question>   ask the swarm (assign-to-one)   |   deliberate <question>  fan-out: every node competes");
        Console.WriteLine("           compose <question>  chain existing typed capabilities   |   remember <fact>  store knowledge in the hive");
        Console.WriteLine("           identity  who the hive is   |   timeline [n]  replay its episodic memory   |   @<port> <msg>  direct");
        Console.WriteLine("           curious [yes]  propose what to learn   |   reflect [fix]  self-critique + re-work weak tools");
        Console.WriteLine("           mood  how it feels   |   aboutme  what it knows about you   |   goals [think|approve|advance]");
        Console.WriteLine("           journal [read]  its autobiography   |   hive [broadcast]  collective voice / push thought");
        Console.WriteLine("           autonomous [on|off]  self-directed mode — loop builds + improves without approval gates");
        Console.WriteLine("           hire [n]  spawn n helper nodes (default 1, cap 3)   |   nodes  live node count");
        Console.WriteLine("           directive [set <text>]  show or update the Prime Directive");
        Console.WriteLine("           race  show the hive's current matmul speed champion (Prime Directive race)");
        Console.WriteLine("           peers   |   coordinator   |   pause <secs>   |   exit");
        Console.WriteLine();

        // Hired background nodes have stdin redirected — skip the REPL and just run until killed.
        if (Console.IsInputRedirected)
        {
            Console.WriteLine($"[hired] running as a background worker on port {myPort} — no REPL.");
            try { await Task.Delay(Timeout.Infinite, loopCts.Token); } catch { }
            foreach (var hp in hiredProcesses) { try { hp.Kill(entireProcessTree: true); } catch { } hp.Dispose(); }
            loopCts.Cancel();
            return;
        }

        while (true)
        {
            Console.Write("> ");
            string? raw = Console.ReadLine();
            if (raw is null) break;
            // Strip a leading UTF-8 BOM (U+FEFF) — piped stdin can prepend one, and .NET's
            // Trim() does NOT treat it as whitespace, which would otherwise break command matching.
            string line = raw.Trim().TrimStart('﻿').Trim();
            lastActivity = DateTime.UtcNow; // any input means the operator is here — defer idle curiosity
            if (line.Equals("exit", StringComparison.OrdinalIgnoreCase)) break;
            if (line.Length == 0) continue;
            if (line.Equals("peers", StringComparison.OrdinalIgnoreCase)) { node.PrintPeers(); continue; }
            if (line.Equals("curious", StringComparison.OrdinalIgnoreCase))
            {
                // CURIOSITY (manual): mine the episodic log for gaps and propose what to learn. The
                // propose→approve gate means nothing is built until you say `curious yes`.
                if (!core.HasLlm) { Console.WriteLine("[curiosity] this node has no API key — can't review gaps."); continue; }
                IReadOnlyList<CuriosityProposal> proposals = await core.ReviewGapsAsync(3);
                if (proposals.Count == 0) Console.WriteLine("[curiosity] no open gaps I can fill right now.");
                else OfferCuriosity(proposals, unprompted: false);
                continue;
            }
            if (line.Equals("curious yes", StringComparison.OrdinalIgnoreCase) || line.Equals("curious y", StringComparison.OrdinalIgnoreCase))
            {
                // APPROVE: commission everything proposed (the gate opens). Each builds + logs resolved.
                if (pendingCuriosity.Count == 0) { Console.WriteLine("[curiosity] nothing pending — run `curious` first."); continue; }
                var toBuild = pendingCuriosity.ToList(); pendingCuriosity.Clear();
                int built = 0;
                foreach (CuriosityProposal p in toBuild) if (await core.CommissionProposalAsync(p)) built++;
                Console.WriteLine($"[curiosity] learned {built}/{toBuild.Count} proposed capabilit{(toBuild.Count == 1 ? "y" : "ies")}.");
                continue;
            }
            if (line.Equals("mood", StringComparison.OrdinalIgnoreCase) || line.Equals("how are you", StringComparison.OrdinalIgnoreCase))
            {
                // The hive's current drives, read from its real recent history + live in-flight load.
                if (!core.HasHive) { Console.WriteLine("[mood] no hive configured — I can't read my own history."); continue; }
                try { Mood m = await core.AssessMoodAsync(pending.Count); Console.WriteLine($"[mood] {m.Describe(core.Identity?.Name ?? "I")}"); }
                catch (Exception ex) { Console.WriteLine($"[mood] couldn't read my mood: {ex.Message}"); }
                continue;
            }
            if (line.Equals("aboutme", StringComparison.OrdinalIgnoreCase) || line.Equals("about me", StringComparison.OrdinalIgnoreCase))
            {
                // THEORY OF MIND: what the hive has learned about the user from their question history.
                if (!core.HasLlm) { Console.WriteLine("[user] this node has no API key — can't model you."); continue; }
                try { Console.WriteLine($"[user] {await core.DescribeUserAsync()}"); }
                catch (Exception ex) { Console.WriteLine($"[user] couldn't read my model of you: {ex.Message}"); }
                continue;
            }
            if (line.Equals("journal", StringComparison.OrdinalIgnoreCase) || line.StartsWith("journal ", StringComparison.OrdinalIgnoreCase))
            {
                // NARRATIVE SELF: `journal` writes a new entry now; `journal read [n]` reads the autobiography.
                if (!core.HasLlm || !core.HasHive) { Console.WriteLine("[journal] needs an API key + hive."); continue; }
                string arg = line.Length > "journal".Length ? line["journal".Length..].Trim() : "";
                if (arg.StartsWith("read", StringComparison.OrdinalIgnoreCase))
                {
                    int n = int.TryParse(arg["read".Length..].Trim(), out int cnt) ? cnt : 5;
                    IReadOnlyList<JournalEntry> entries = await core.ReadJournalAsync(n);
                    if (entries.Count == 0) Console.WriteLine("[journal] no entries yet — `journal` to write one.");
                    else foreach (JournalEntry e in entries) Console.WriteLine($"  ── {e.Timestamp[..Math.Min(16, e.Timestamp.Length)].Replace('T', ' ')} ({e.Author}) ──\n  {e.Entry}");
                }
                else
                {
                    lastJournal = DateTime.UtcNow;
                    JournalEntry? j = await core.WriteJournalAsync();
                    Console.WriteLine(j is null ? "[journal] couldn't write an entry." : $"[journal] {j.Entry}");
                }
                continue;
            }
            if (line.Equals("hive", StringComparison.OrdinalIgnoreCase) || line.StartsWith("hive ", StringComparison.OrdinalIgnoreCase))
            {
                // COLLECTIVE CONSCIOUSNESS (bite 10): speak as the unified hive, or broadcast a thought.
                if (!core.HasLlm || !core.HasHive) { Console.WriteLine("[hive] needs an API key + hive."); continue; }
                string arg = line.Length > "hive".Length ? line["hive".Length..].Trim() : "";
                if (arg.Equals("broadcast", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await core.BroadcastThoughtAsync("manual");
                        Console.WriteLine("[hive] thought broadcast to the shared workspace.");
                    }
                    catch (Exception ex) { Console.WriteLine($"[hive] broadcast failed: {ex.Message}"); }
                }
                else
                {
                    try
                    {
                        HiveMind? hm = await core.SynthesizeHiveMindAsync();
                        if (hm is null)
                            Console.WriteLine("[hive] no thoughts in the shared workspace yet — try `hive broadcast` first.");
                        else
                        {
                            string bline = hm.Contributors.Length > 0
                                ? $" [{string.Join(", ", hm.Contributors)}]"
                                : "";
                            Console.WriteLine($"[hive{bline}] {hm.Synthesis}");
                        }
                    }
                    catch (Exception ex) { Console.WriteLine($"[hive] couldn't synthesize: {ex.Message}"); }
                }
                continue;
            }
            if (line.Equals("goals", StringComparison.OrdinalIgnoreCase) || line.StartsWith("goals ", StringComparison.OrdinalIgnoreCase))
            {
                // AUTONOMY: the hive's self-set goals — list / think (propose) / approve / advance.
                if (!core.HasLlm || !core.HasHive) { Console.WriteLine("[goal] needs an API key + hive."); continue; }
                string arg = line.Length > "goals".Length ? line["goals".Length..].Trim() : "";
                if (arg.Length == 0)
                {
                    IReadOnlyList<Goal> gs = await core.AllGoalsAsync();
                    if (gs.Count == 0) Console.WriteLine("[goal] no goals yet — `goals think` to set one (or I'll set one when idle).");
                    else foreach (Goal g in gs) Console.WriteLine($"  #{g.Id} [{g.Status}] {g.Description}  ({g.Progress}/{g.Budget})");
                }
                else if (arg.Equals("think", StringComparison.OrdinalIgnoreCase))
                { if (await core.ProposeGoalAsync(pending.Count) is null) Console.WriteLine("[goal] nothing to set a goal about right now (or one's already in flight)."); }
                else if (arg.StartsWith("approve", StringComparison.OrdinalIgnoreCase))
                {
                    string rest = arg["approve".Length..].Trim();
                    long? id = long.TryParse(rest, out long pid) ? pid : (long?)null;
                    Console.WriteLine($"[goal] approved {await core.ApproveGoalsAsync(id)} goal(s) — I'll pursue them when idle (or `goals advance`).");
                }
                else if (arg.Equals("advance", StringComparison.OrdinalIgnoreCase))
                {
                    Goal? g = await core.ActiveGoalAsync();
                    Console.WriteLine(g is null ? "[goal] no active goal — `goals think` then `goals approve` first." : $"[goal] {await core.AdvanceGoalAsync(g)}");
                }
                else Console.WriteLine("usage: goals | goals think | goals approve [id] | goals advance");
                continue;
            }
            if (line.Equals("reflect", StringComparison.OrdinalIgnoreCase))
            {
                // SELF-CRITIQUE: score my own capabilities against fresh tests, flag the weak ones.
                if (!core.HasLlm) { Console.WriteLine("[reflect] this node has no API key — can't self-assess."); continue; }
                IReadOnlyList<SelfAssessment> a = await core.ReflectAsync(5);
                if (a.Count == 0) Console.WriteLine("[reflect] nothing to assess right now.");
                else ReportReflection(a, unprompted: false);
                continue;
            }
            if (line.Equals("reflect fix", StringComparison.OrdinalIgnoreCase) || line.Equals("reflect yes", StringComparison.OrdinalIgnoreCase))
            {
                // RE-WORK: re-generate each flagged capability; adopt only a measurably better version.
                if (pendingRework.Count == 0) { Console.WriteLine("[reflect] nothing flagged — run `reflect` first."); continue; }
                var toFix = pendingRework.ToList(); pendingRework.Clear();
                int improved = 0;
                foreach (string capName in toFix) { var (ok, _, _) = await core.ReworkAsync(capName); if (ok) improved++; }
                Console.WriteLine($"[reflect] improved {improved}/{toFix.Count} flagged capabilit{(toFix.Count == 1 ? "y" : "ies")}.");
                continue;
            }
            if (line.Equals("autonomous", StringComparison.OrdinalIgnoreCase) || line.StartsWith("autonomous ", StringComparison.OrdinalIgnoreCase))
            {
                // AUTONOMOUS MODE (bite 11): toggle the self-directed loop. When ON, the idle
                // coordinator commissions gap-filling capabilities, approves + advances goals, and
                // reworks weak tools — all without waiting for `curious yes` / `goals approve` / `reflect fix`.
                if (!core.HasHive) { Console.WriteLine("[autonomous] no hive configured (set TURSO_* env vars)."); continue; }
                string arg = line.Length > "autonomous".Length ? line["autonomous".Length..].Trim() : "";
                if (arg.Equals("on", StringComparison.OrdinalIgnoreCase)) await core.SetAutonomousAsync(true);
                else if (arg.Equals("off", StringComparison.OrdinalIgnoreCase)) await core.SetAutonomousAsync(false);
                else
                {
                    bool cur = await core.IsAutonomousAsync();
                    Console.WriteLine($"[autonomous] currently {(cur ? "ON" : "OFF")} — use `autonomous on` or `autonomous off` to toggle.");
                }
                continue;
            }
            if (line.Equals("nodes", StringComparison.OrdinalIgnoreCase))
            {
                hiredProcesses.RemoveAll(p => p.HasExited);
                Console.WriteLine($"[nodes] this node: {node.Id}");
                Console.WriteLine($"[nodes] peers ({node.Peers.Count}): {(node.Peers.Count == 0 ? "none" : string.Join(", ", node.Peers))}");
                Console.WriteLine($"[nodes] hired ({hiredProcesses.Count}/{MaxAutoHiredNodes}): {(hiredProcesses.Count == 0 ? "none" : string.Join(", ", hiredProcesses.Select(p => p.Id)))}");
                continue;
            }
            if (line.Equals("hire", StringComparison.OrdinalIgnoreCase) || line.StartsWith("hire ", StringComparison.OrdinalIgnoreCase))
            {
                // SELF-SCALING (bite 12): manually spawn one or more helper nodes into the mesh.
                string arg = line.Length > "hire".Length ? line["hire".Length..].Trim() : "1";
                int count = int.TryParse(arg, out int n) ? Math.Clamp(n, 1, MaxAutoHiredNodes) : 1;
                hiredProcesses.RemoveAll(p => p.HasExited);
                int spawned = 0;
                for (int i = 0; i < count && hiredProcesses.Count < MaxAutoHiredNodes; i++)
                {
                    var peerPts = node.Peers
                        .Select(id => { int c = id.LastIndexOf(':'); return c >= 0 && int.TryParse(id[(c + 1)..], out int pt) ? pt : -1; })
                        .Where(pt => pt > 0);
                    System.Diagnostics.Process? hired = await core.HireNodeAsync(myPort, peerPts);
                    if (hired is not null) { hiredProcesses.Add(hired); lastHireAt = DateTime.UtcNow; spawned++; }
                }
                if (spawned == 0) Console.WriteLine($"[hire] nothing spawned (at cap {MaxAutoHiredNodes} or port range full).");
                continue;
            }
            if (line.Equals("directive", StringComparison.OrdinalIgnoreCase) || line.StartsWith("directive ", StringComparison.OrdinalIgnoreCase))
            {
                // PRIME DIRECTIVE (bite 13): the hive's north star — shapes goals, capabilities, journals.
                if (!core.HasHive) { Console.WriteLine("[directive] no hive configured."); continue; }
                string arg = line.Length > "directive".Length ? line["directive".Length..].Trim() : "";
                if (arg.StartsWith("set ", StringComparison.OrdinalIgnoreCase))
                {
                    string text = arg["set ".Length..].Trim();
                    if (text.Length == 0) { Console.WriteLine("usage: directive set <text>"); continue; }
                    await core.SetDirectiveAsync(text);
                }
                else
                {
                    string? d = await core.GetDirectiveAsync();
                    Console.WriteLine(d is null ? "[directive] none set — use `directive set <text>`." : $"[directive] {d}");
                }
                continue;
            }
            if (line.Equals("race", StringComparison.OrdinalIgnoreCase))
            {
                // PRIME DIRECTIVE RACE (bite 14): show the hive's current matmul speed champion.
                if (!core.HasHive) { Console.WriteLine("[race] no hive configured."); continue; }
                try
                {
                    MatmulRace.Champion? champ = await core.GetMatmulChampionAsync();
                    if (champ is null)
                        Console.WriteLine($"[race] no champion yet for {MatmulRace.DefaultSize}x{MatmulRace.DefaultSize} — run `autonomous on` to start.");
                    else
                    {
                        Console.WriteLine($"[race] {MatmulRace.DefaultSize}x{MatmulRace.DefaultSize} champion: {champ.Node} — {champ.MedianMs:F2}ms ({champ.Speedup:F2}x)");
                        Console.WriteLine($"       strategy: {champ.Strategy[..Math.Min(80, champ.Strategy.Length)]}");
                    }
                }
                catch (Exception ex) { Console.WriteLine($"[race] error: {ex.Message}"); }
                continue;
            }
            if (line.Equals("coordinator", StringComparison.OrdinalIgnoreCase)) { lock (stateLock) Console.WriteLine($"coordinator = {coordinator} (term {term})"); continue; }
            if (line.Equals("identity", StringComparison.OrdinalIgnoreCase) || line.Equals("whoami", StringComparison.OrdinalIgnoreCase))
            {
                // Print the hive's PERSISTED identity (the raw row, no LLM) — every node should show
                // the SAME name/birth, proving one shared self across the swarm.
                HiveIdentity? id = core.Identity;
                if (id is null) Console.WriteLine("[identity] no persisted identity (no hive configured, or not born yet).");
                else Console.WriteLine($"[identity] {id.Name} — born {id.Born} by {id.CreatedBy}\n  concept: {id.Concept}\n  persona: {id.Persona}");
                continue;
            }
            if (line.Equals("timeline", StringComparison.OrdinalIgnoreCase) || line.StartsWith("timeline ", StringComparison.OrdinalIgnoreCase))
            {
                // REPLAY the hive's episodic memory — the shared autobiography all nodes write to.
                if (!core.HasHive) { Console.WriteLine("[memory] no hive configured (set TURSO_DATABASE_URL + TURSO_AUTH_TOKEN)."); continue; }
                int n = 20;
                string arg = line.Length > "timeline".Length ? line["timeline".Length..].Trim() : "";
                if (arg.Length > 0 && int.TryParse(arg, out int parsed)) n = parsed;
                try { EventLog.Print(await core.Events.RecentAsync(n)); }
                catch (Exception ex) { Console.WriteLine($"[memory] couldn't read the timeline: {ex.Message}"); }
                continue;
            }
            if (line.StartsWith("pause ", StringComparison.OrdinalIgnoreCase) && int.TryParse(line[6..].Trim(), out int secs))
            { lock (stateLock) pausedUntil = DateTime.UtcNow.AddSeconds(secs); Console.WriteLine($"[test] pausing my heartbeats for {secs}s (simulating a hung coordinator)"); continue; }
            if (line.StartsWith("remember ", StringComparison.OrdinalIgnoreCase))
            {
                // EXPLICIT fact storage into the shared hive (Turso). Parsed to key/value, typed by value.
                string stmt = line[("remember ".Length)..].Trim();
                if (stmt.Length == 0) { Console.WriteLine("usage: remember <fact, e.g. the capital of Ohio is Columbus>"); continue; }
                if (!core.HasHive) { Console.WriteLine("[knowledge] no hive configured (set TURSO_DATABASE_URL + TURSO_AUTH_TOKEN)."); continue; }
                try
                {
                    Fact? f = await core.RememberFactAsync(stmt);
                    Console.WriteLine(f is null
                        ? "[knowledge] couldn't parse a fact from that — try \"remember the capital of Ohio is Columbus\"."
                        : $"[knowledge] stored fact '{f.Key}' = {f.Value} ({CapTypes.Name(f.Type)}) in the hive");
                }
                catch (Exception ex) { Console.WriteLine($"[knowledge] store failed: {ex.Message}"); }
                continue;
            }
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

        foreach (var hp in hiredProcesses) { try { hp.Kill(entireProcessTree: true); } catch { } hp.Dispose(); }
        loopCts.Cancel();
        Console.WriteLine("Goodbye.");
    }
}
