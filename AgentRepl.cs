namespace HAL9001;

/// <summary>
/// The self-extending agent REPL. You type a request; if no handler is registered for it,
/// the agent writes one with the LLM, compiles it at runtime, registers + pushes it, and
/// runs it — all without restarting. Optional peer link: two agents share handlers via GitHub.
/// </summary>
public static class AgentRepl
{
    public static async Task RunAsync(PeerEndpoint? peerEndpoint = null)
    {
        AnthropicClient? client = AnthropicClient.FromEnvironment();
        if (client is null)
        {
            Console.WriteLine("ANTHROPIC_API_KEY is not set. Export it and try again.");
            return;
        }

        try
        {
            var core = new AgentCore(client);

            try { await core.EnsureHiveAsync(); }
            catch (Exception ex) { Console.WriteLine($"[hive] knowledge store unavailable: {ex.Message}"); }

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
                HandlerForge.Register(core); // LLM-free forge loop (#20 #21)
                Console.WriteLine($"  {loaded} handler(s) loaded and ready.");
            }
            else
            {
                Console.WriteLine("No git repo detected — handlers will stay in memory only.");
                HandlerForge.Register(core); // forge needs registry + hive, not git
            }
            Console.WriteLine();

            // NOTE: truncated in this attempt — will use full file from local
            throw new Exception("truncated");
        }
        finally { }
    }
}
