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
            var registry = new HandlerRegistry();

            GitSync? git = GitSync.Discover();
            var generator = new HandlerGenerator(client, registry, git);
            var router = new CapabilityRouter(client, registry);

            PeerNode? peer = null;

            // Local requests (the REPL thread) and peer questions (the socket's background
            // receive thread) both run through ProduceAnswerAsync, which touches the shared
            // registry + generator. This gate serializes the two so they never corrupt that
            // shared state or interleave a half-built handler.
            var requestGate = new SemaphoreSlim(1, 1);

            // ── Shared answer path (Rung 1a) ─────────────────────────────────────────────
            // ROUTE (which capability is this?) → use the existing one OR commission a new
            // general one → RUN it to get the answer. The LLM recognizes/commissions; it
            // never answers — the answer is always the output of running compiled code.
            // Used identically by a locally-typed request AND a peer question.
            // Returns the answer plus the NAME of the capability used (so the app can pick a
            // *different* capability for the follow-up). Both null if nothing could be produced.
            async Task<(string? Answer, string? Capability)> ProduceAnswerAsync(string request)
            {
                await requestGate.WaitAsync();
                try
                {
                    RouteDecision decision = await router.RouteAsync(request);

                    // NOT A TASK → reply conversationally and touch NOTHING in the
                    // handler/compile/push pipeline. capabilityUsed stays null, so no
                    // follow-up fires either. Works for local input and peer input alike.
                    if (decision.Action == RouteAction.Decline)
                    {
                        Console.WriteLine("  (not a task — replying conversationally; nothing generated)");
                        return (decision.Reply, null);
                    }

                    IHandler? handler;
                    string usedName;
                    if (decision.Action == RouteAction.UseExisting &&
                        registry.TryGet(decision.Name, out handler))
                    {
                        usedName = decision.Name;
                        Console.WriteLine($"  (using capability '{usedName}')");
                    }
                    else
                    {
                        // CreateNew — or UseExisting that named something we don't actually
                        // have (LLM slip): commission a general capability either way.
                        string capName = decision.Name.Length > 0 ? decision.Name : "capability";
                        string capDesc = decision.Description.Length > 0 ? decision.Description : request;
                        usedName = capName;
                        Console.WriteLine($"  (no capability yet — commissioning '{capName}': {capDesc})");
                        try
                        {
                            handler = await generator.GenerateAsync(capName, capDesc, request);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"  generation failed: {ex.Message}");
                            return (null, null);
                        }
                        if (handler is null)
                            return (null, null); // didn't compile even after the retry; details printed
                    }

                    // Run the compiled capability with a timeout, so a hung network call in
                    // generated code can't freeze the agent, and catch any runtime throw.
                    try
                    {
                        string result = await Task.Run(() => handler!.Handle(request))
                                                  .WaitAsync(TimeSpan.FromSeconds(30));
                        return (result, usedName);
                    }
                    catch (TimeoutException)
                    {
                        return ("(the capability took too long to run and was cancelled)", usedName);
                    }
                    catch (Exception ex)
                    {
                        // Surface the runtime error and stay alive — never crash the agent.
                        return ($"(the capability errored at runtime: {ex.GetBaseException().Message})", usedName);
                    }
                }
                finally
                {
                    requestGate.Release();
                }
            }

            // App-generated follow-up (NO LLM): pick a DIFFERENT capability from the catalog
            // and replay its example request. The agent's follow-ups are thus grounded in what
            // it can actually do. Returns null when there's nothing relatable to ask yet
            // (e.g. it only has the one capability it just used).
            string? BuildFollowUp(string? justUsed)
            {
                var candidates = registry.Catalog()
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

            if (git is not null)
            {
                Console.WriteLine("Generated handlers will be pushed to:");
                git.PrintRemoteAndBranch();
                Console.WriteLine("Syncing existing handlers from GitHub...");
                git.Pull();
                HandlerLoader.LoadAll(git.HandlersDirectory, registry);
                Console.WriteLine($"  {registry.Count} handler(s) loaded and ready.");
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
