using System.Text;

namespace HAL9001;

/// <summary>
/// Step 3's main experience: the self-extending REPL. You type a request; if no handler
/// is registered for it, the agent writes one with the LLM, compiles it at runtime, and
/// uses it to answer — then suggests a follow-up question. All local, one instance.
/// </summary>
public static class AgentRepl
{
    public static async Task RunAsync()
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

            // Step 4: locate the git repo so generated handlers can be pushed. Null if we're
            // somehow not inside a repo — generation still works, just stays in memory.
            GitSync? git = GitSync.Discover();
            var generator = new HandlerGenerator(client, registry, git);

            Console.WriteLine("HAL9001 — self-extending agent (local, single instance)");
            Console.WriteLine($"Model: {AnthropicClient.Model}");
            if (git is not null)
            {
                Console.WriteLine("Generated handlers will be pushed to:");
                git.PrintRemoteAndBranch();
            }
            else
            {
                Console.WriteLine("No git repo detected — handlers will stay in memory only.");
            }
            Console.WriteLine();
            Console.WriteLine("Type a request. If I have no handler for it, I'll write one, compile it,");
            Console.WriteLine("and answer. Repeating a request reuses the handler I already built.");
            Console.WriteLine("Type 'exit' to quit.");
            Console.WriteLine();

            while (true)
            {
                Console.Write("> ");
                string? line = Console.ReadLine();

                if (line is null || line.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase))
                    break;

                string request = line.Trim();
                if (request.Length == 0) continue;

                // The registry key is derived from the request, so the same request maps to
                // the same handler across the session.
                string name = DeriveName(request);

                // ── 1) Check the registry — "do I already know how to do this?" ──────────
                if (!registry.TryGet(name, out IHandler? handler))
                {
                    Console.WriteLine($"  (no handler '{name}' yet — generating one with the LLM...)");
                    try
                    {
                        handler = await generator.GenerateAsync(name, request);
                    }
                    catch (Exception ex)
                    {
                        // e.g. network error, bad API key, refusal — stay alive.
                        Console.WriteLine($"  generation failed: {ex.Message}");
                        Console.WriteLine();
                        continue;
                    }

                    if (handler is null)
                    {
                        // Couldn't compile even after the retry; details already printed.
                        Console.WriteLine();
                        continue;
                    }
                }
                else
                {
                    Console.WriteLine($"  (reusing handler '{name}')");
                }

                // ── 2) Answer with the (possibly brand-new) compiled handler ─────────────
                string answer = handler.Handle(request);
                Console.WriteLine($"  answer: {answer}");

                // ── 3) Generate + print a follow-up question (local only for now) ────────
                try
                {
                    string followUp = await generator.GenerateFollowUpAsync(request, answer);
                    if (followUp.Length > 0)
                        Console.WriteLine($"  follow-up: {followUp}");
                }
                catch
                {
                    // Follow-up is a nice-to-have; never let it break the loop.
                }

                Console.WriteLine();
            }

            Console.WriteLine("Goodbye.");
        }
    }

    /// <summary>
    /// Derive a tidy registry name from a request: first few words, lowercased, with
    /// anything that isn't a letter/digit turned into a hyphen. e.g.
    /// "Reverse this string please" -> "reverse-this-string-please".
    /// </summary>
    private static string DeriveName(string request)
    {
        string[] words = request.ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        var slug = new StringBuilder();
        foreach (string word in words.Take(4))
        {
            if (slug.Length > 0) slug.Append('-');
            foreach (char c in word)
                if (char.IsLetterOrDigit(c)) slug.Append(c);
        }

        string name = slug.ToString().Trim('-');
        return name.Length == 0 ? "handler" : name;
    }
}
