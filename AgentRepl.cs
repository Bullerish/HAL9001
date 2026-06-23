namespace HAL9001;

/// <summary>
/// The self-extending agent REPL. You type a request; if no handler is registered for it,
/// the agent writes one with the LLM, compiles it at runtime, registers + pushes it, and
/// answers — then suggests a follow-up.
///
/// With a peer link (`agent host`/`agent join`):
///   • a locally-generated follow-up is SENT to the peer as a Question (sub-step A), and
///   • an incoming peer Question is routed through the SAME answer path and the result is
///     sent back as an Answer (sub-step B).
/// One round only: an arriving Answer is displayed and the chain STOPS — no automatic
/// re-question — so two instances can't volley forever and burn tokens.
/// </summary>
public static class AgentRepl
{
    public static async Task RunAsync(PeerEndpoint? peerEndpoint = null)
    {
        // The whole loop needs an API key. Fail friendly if it's missing.
        AnthropicClient? client = AnthropicClient.FromEnvironment();
        if (client is null)
        {
            Console.WriteLine("ANTHROPIC_API_KEY is not set, so the agent can't generate handlers.");
            Console.WriteLine("Set it for this terminal, then re-run:");
            Console.WriteLine("  PowerShell:  $env:ANTHROPIC_API_KEY = \"sk-ant-...\"");
            Console.WriteLine("  bash:        export ANTHROPIC_API_KEY=sk-ant-...");
            Console.WriteLine();
            Console.WriteLine($"(Model in use: {AnthropicClient.Model})");
            return;
        }

        using (client)
        {
            // The whole answer path — routing, generation, compilation, registry, push — now
            // lives in the SHARED AgentCore (same implementation the swarm uses). This REPL keeps
            // only its own concerns: the peer link, the one-round loop guard, and follow-ups.
            var core = new AgentCore(client);
            // Bootstrap the shared hive (facts + episodic memory) so this lone agent's significant
            // acts are recorded too. No-op without Turso configured. (Actor stays the default "single".)
            try { await core.EnsureHiveAsync(); }
            catch (Exception ex) { Console.WriteLine($"[hive] store unavailable: {ex.Message}"); }

            // Curiosity proposals awaiting `curious yes`, and weak capabilities flagged for `reflect fix`.
            var pendingCuriosity = new List<CuriosityProposal>();
            var pendingRework = new List<string>();

            PeerNode? peer = null;

            // Thin wrapper over the shared answer path that preserves this REPL's contract:
            // returns (answer, capabilityUsed). A decline returns the reply with a null capability
            // (so no follow-up fires); a generation failure returns (null, null) so the loop just
            // moves on; an answer returns the text plus the capability's name. Local requests (the
            // REPL thread) and peer questions (the socket's receive thread) both come through here;
            // AgentCore's internal gate serializes them over the shared registry/generator.
            async Task<(string? Answer, string? Capability)> ProduceAnswerAsync(string request)
            {
                AnswerResult r = await core.AnswerAsync(request);
                switch (r.Kind)
                {
                    case AnswerKind.Declined:
                        Console.WriteLine("  (not a task — replying conversationally; nothing generated)");
                        return (r.Text, null);
                    case AnswerKind.GenerationFailed:
                        Console.WriteLine($"  {r.Text}");
                        return (null, null);
                    default: // Answered
                        return (r.Text, r.Capability);
                }
            }

            // App-generated follow-up (NO LLM): pick a DIFFERENT capability from the catalog
            // and replay its example request. The agent's follow-ups are thus grounded in what
            // it can actually do. Returns null when there's nothing relatable to ask yet
            // (e.g. it only has the one capability it just used).
            string? BuildFollowUp(string? justUsed)
            {
                var candidates = core.Registry.Catalog()
                    .Where(c => c.ExampleRequest.Length > 0)
                    .Where(c => !string.Equals(c.Name, justUsed, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                return candidates.Count == 0
                    ? null
                    : candidates[Random.Shared.Next(candidates.Count)].ExampleRequest;
            }

            // ── Peer message handler ─────────────────────────────────────────────────────
            // Runs on the socket's background thread (fire-and-forget from the receive loop),
            // so it owns its own try/catch — a peer hiccup must never crash the agent.
            async Task HandlePeerMessageAsync(PeerMessage message)
            {
                try
                {
                    switch (message.Kind)
                    {
                        case PeerMessageKind.Question:
                            // Sub-step B: route the peer's question into our own agent path,
                            // then send the result back as an Answer.
                            Console.WriteLine($"\n[peer asks] {message.Text}");
                            Console.WriteLine("  routing peer input through my agent...");
                            // Same 3-way path as local input: a peer "hello" classifies as a
                            // decline and gets a conversational reply — not a forced handler.
                            var (peerAns, _) = await ProduceAnswerAsync(message.Text);
                            string reply = peerAns ?? "(sorry — I couldn't produce a reply for that)";
                            Console.WriteLine($"  replying to peer: {reply}");
                            await peer!.SendAsync(PeerMessageKind.Answer, reply);
                            Console.WriteLine("  [sent reply to peer]");
                            Console.Write("> ");
                            break;

                        case PeerMessageKind.Answer:
                            // LOOP GUARD: display and stop. Do NOT auto-generate a new
                            // follow-up — otherwise A and B would answer each other forever.
                            Console.WriteLine($"\n[peer answered] {message.Text}");
                            Console.Write("> ");
                            break;

                        default: // Chat
                            Console.WriteLine($"\n[peer] {message.Text}");
                            Console.Write("> ");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"\n[peer handling error: {ex.Message}]");
                }
            }

            // ── Startup banner + handler sync ────────────────────────────────────────────
            Console.WriteLine(peerEndpoint is null
                ? "HAL9001 — self-extending agent (single instance)"
                : "HAL9001 — self-extending agent (peer-linked)");
            Console.WriteLine($"Model: {AnthropicClient.Model}");

            if (core.Git is not null)
            {
                Console.WriteLine("Generated handlers will be pushed to:");
                core.Git.PrintRemoteAndBranch();
                Console.WriteLine("Syncing existing handlers from GitHub...");
                int loaded = core.LoadSharedHandlers();
                Console.WriteLine($"  {loaded} handler(s) loaded and ready.");
            }
            else
            {
                Console.WriteLine("No git repo detected — handlers will stay in memory only.");
            }
            Console.WriteLine();

            // ── Optional peer link ───────────────────────────────────────────────────────
            if (peerEndpoint is not null)
            {
                peer = new PeerNode();
                // Fire-and-forget: the handler has its own try/catch; discarding the Task
                // keeps the receive loop free to read the next message.
                peer.MessageReceived += message => _ = HandlePeerMessageAsync(message);
                peer.PeerDisconnected += () => Console.WriteLine("\n[peer] disconnected.");

                if (peerEndpoint.IsHost)
                    await peer.ListenAndAcceptAsync(peerEndpoint.Port);
                else
                    await peer.ConnectAsync(peerEndpoint.RemoteHost, peerEndpoint.Port);

                Console.WriteLine("Linked to peer — I'll send my follow-ups to it and answer its questions.");
                Console.WriteLine();
            }

            Console.WriteLine("Type a request. If I have no handler for it, I'll write one, compile it,");
            Console.WriteLine("and answer. Repeating a request reuses the handler I already built.");
            Console.WriteLine("Type 'exit' to quit.");
            Console.WriteLine();

            // ── REPL loop (locally-typed requests) ───────────────────────────────────────
            while (true)
            {
                Console.Write("> ");
                string? line = Console.ReadLine();

                if (line is null || line.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase))
                    break;

                string request = line.Trim();
                if (request.Length == 0) continue;

                // CURIOSITY (sentience bite 4): review noticed gaps + propose what to learn, gated.
                if (request.Equals("curious", StringComparison.OrdinalIgnoreCase))
                {
                    IReadOnlyList<CuriosityProposal> proposals = await core.ReviewGapsAsync(3);
                    if (proposals.Count == 0) { Console.WriteLine("  [curiosity] no open gaps I can fill right now."); continue; }
                    pendingCuriosity = proposals.ToList();
                    Console.WriteLine("  [curiosity] looking back at what I couldn't do, I could learn:");
                    foreach (CuriosityProposal p in proposals)
                        Console.WriteLine($"    • for \"{p.Request}\" → '{p.Name}' [{CapTypes.Name(p.InputType)}→{CapTypes.Name(p.OutputType)}, {StabilityKinds.Name(p.Stability)}]: {p.Description}");
                    Console.WriteLine("    approve with `curious yes`.");
                    continue;
                }
                if (request.Equals("curious yes", StringComparison.OrdinalIgnoreCase) || request.Equals("curious y", StringComparison.OrdinalIgnoreCase))
                {
                    if (pendingCuriosity.Count == 0) { Console.WriteLine("  [curiosity] nothing pending — run `curious` first."); continue; }
                    var toBuild = pendingCuriosity.ToList(); pendingCuriosity.Clear();
                    int built = 0;
                    foreach (CuriosityProposal p in toBuild) if (await core.CommissionProposalAsync(p)) built++;
                    Console.WriteLine($"  [curiosity] learned {built}/{toBuild.Count} proposed capabilit{(toBuild.Count == 1 ? "y" : "ies")}.");
                    continue;
                }

                // SELF-CRITIQUE (sentience bite 5): score my own capabilities, flag + re-work the weak.
                if (request.Equals("reflect", StringComparison.OrdinalIgnoreCase))
                {
                    IReadOnlyList<SelfAssessment> a = await core.ReflectAsync(5);
                    if (a.Count == 0) { Console.WriteLine("  [reflect] nothing to assess right now."); continue; }
                    pendingRework = a.Where(AgentCore.IsWeak).Select(x => x.Name).ToList();
                    Console.WriteLine("  [reflect] I checked my own work:");
                    foreach (SelfAssessment x in a)
                        Console.WriteLine($"    • {x.Name}: confidence {x.Confidence:0.00} ({x.Passed}/{x.Total}){(AgentCore.IsWeak(x) ? "  ⚠ weak" : "")}");
                    if (pendingRework.Count > 0) Console.WriteLine($"    weak: {string.Join(", ", pendingRework)} — re-work with `reflect fix`.");
                    continue;
                }
                if (request.Equals("reflect fix", StringComparison.OrdinalIgnoreCase) || request.Equals("reflect yes", StringComparison.OrdinalIgnoreCase))
                {
                    if (pendingRework.Count == 0) { Console.WriteLine("  [reflect] nothing flagged — run `reflect` first."); continue; }
                    var toFix = pendingRework.ToList(); pendingRework.Clear();
                    int improved = 0;
                    foreach (string capName in toFix) { var (ok, _, _) = await core.ReworkAsync(capName); if (ok) improved++; }
                    Console.WriteLine($"  [reflect] improved {improved}/{toFix.Count} flagged capabilit{(toFix.Count == 1 ? "y" : "ies")}.");
                    continue;
                }

                // Same path a peer input uses.
                var (answer, usedCapability) = await ProduceAnswerAsync(request);
                if (answer is null)
                {
                    Console.WriteLine();
                    continue;
                }

                // usedCapability == null means it was a conversational decline; print the
                // reply plainly. Otherwise it's a capability result.
                Console.WriteLine(usedCapability is null ? $"  {answer}" : $"  answer: {answer}");

                // Follow-up ONLY after we actually used/built a capability — never after a
                // conversational decline. App-generated (no LLM): replay a different
                // capability's example. Null until we have a second capability to ask about.
                if (usedCapability is not null)
                {
                    string? followUp = BuildFollowUp(usedCapability);
                    if (followUp is not null)
                    {
                        Console.WriteLine($"  follow-up (from what I can do): {followUp}");
                        if (peer is not null)
                        {
                            try
                            {
                                await peer.SendAsync(PeerMessageKind.Question, followUp);
                                Console.WriteLine("  [sent follow-up to peer]");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"  [could not send to peer: {ex.Message}]");
                            }
                        }
                    }
                }

                Console.WriteLine();
            }

            Console.WriteLine("Goodbye.");

            // Close the peer link (cancels its receive loop, closes the socket) so the other
            // instance sees a clean disconnect.
            if (peer is not null) await peer.DisposeAsync();
        }
    }

}

/// <summary>
/// How an agent instance should link to a peer — the Step 2 host/join roles.
/// IsHost = listen on Port; otherwise connect to RemoteHost:Port.
/// </summary>
public sealed record PeerEndpoint(bool IsHost, string RemoteHost, int Port);
