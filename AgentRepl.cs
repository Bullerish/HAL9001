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
            async Task<string?> ProduceAnswerAsync(string request)
            {
                await requestGate.WaitAsync();
                try
                {
                    RouteDecision decision = await router.RouteAsync(request);

                    IHandler? handler;
                    if (decision.Action == RouteAction.UseExisting &&
                        registry.TryGet(decision.Name, out handler))
                    {
                        Console.WriteLine($"  (using capability '{decision.Name}')");
                    }
                    else
                    {
                        // CreateNew — or UseExisting that named something we don't actually
                        // have (LLM slip): commission a general capability either way.
                        string capName = decision.Name.Length > 0 ? decision.Name : "capability";
                        string capDesc = decision.Description.Length > 0 ? decision.Description : request;
                        Console.WriteLine($"  (no capability yet — commissioning '{capName}': {capDesc})");
                        try
                        {
                            handler = await generator.GenerateAsync(capName, capDesc, request);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"  generation failed: {ex.Message}");
                            return null;
                        }
                        if (handler is null)
                            return null; // didn't compile even after the retry; details already printed
                    }

                    // Run the compiled capability with a timeout, so a hung network call in
                    // generated code can't freeze the agent. (We can't cancel synchronous
                    // code mid-run, but we stop waiting and report.)
                    try
                    {
                        return await Task.Run(() => handler!.Handle(request))
                                         .WaitAsync(TimeSpan.FromSeconds(30));
                    }
                    catch (TimeoutException)
                    {
                        return "(the capability took too long to run and was cancelled)";
                    }
                }
                finally
                {
                    requestGate.Release();
                }
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
                            Console.WriteLine("  routing peer question into my agent...");
                            string answer = await ProduceAnswerAsync(message.Text)
                                             ?? "(sorry — I couldn't generate an answer for that)";
                            Console.WriteLine($"  answering peer: {answer}");
                            await peer!.SendAsync(PeerMessageKind.Answer, answer);
                            Console.WriteLine("  [sent answer to peer]");
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

                // Same answer path a peer question uses.
                string? answer = await ProduceAnswerAsync(request);
                if (answer is null)
                {
                    Console.WriteLine();
                    continue;
                }
                Console.WriteLine($"  answer: {answer}");

                // Generate a follow-up question; print it AND send it to the peer (sub-step A).
                // This happens ONLY for locally-typed requests — answering a peer's question
                // never generates a follow-up (that's the loop guard).
                try
                {
                    string followUp = await generator.GenerateFollowUpAsync(request, answer);
                    if (followUp.Length > 0)
                    {
                        Console.WriteLine($"  follow-up: {followUp}");
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
                catch
                {
                    // Follow-up is a nice-to-have; never let it break the loop.
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
