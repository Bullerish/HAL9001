using System.Text.Json;

namespace HAL9001;

/// <summary>What the classifier decided to do with an input — the three-way decision.</summary>
public enum RouteAction
{
    UseExisting, // input clearly matches an existing capability → route to it
    CreateNew,   // a genuine task with no existing capability → commission a new general one
    Decline,     // NOT a task (greeting, chitchat, vague) → reply conversationally, build nothing
}

/// <summary>
/// The classifier's decision.
///   UseExisting → Name is the existing capability.
///   CreateNew   → Name is a proposed id, Description the one-line spec of the GENERAL capability.
///   Decline     → Reply is a short conversational response; nothing is built.
/// </summary>
public sealed record RouteDecision(RouteAction Action, string Name, string Description, string Reply);

/// <summary>
/// The "recognize, don't match" step. Instead of slug-matching a request's literal text,
/// it asks the LLM to classify the input three ways by MEANING: reuse an existing
/// capability, commission a new general one, or DECLINE (it isn't a task — reply
/// conversationally and build nothing). The decline branch is what stops greetings and
/// chitchat from being force-matched to the nearest handler.
///
/// Crucially, the router NEVER answers a task. It only decides which tool is needed (and,
/// for a decline, supplies a short social reply — not a task answer).
/// </summary>
public sealed class CapabilityRouter
{
    private readonly AnthropicClient _client;
    private readonly HandlerRegistry _registry;

    public CapabilityRouter(AnthropicClient client, HandlerRegistry registry)
    {
        _client = client;
        _registry = registry;
    }

    private const string SystemPrompt = """
        You are the router for a tool-building agent. You decide what to DO with an input —
        you NEVER answer a task yourself. You are given the input and a catalog of existing
        capabilities (each "name: description"). Choose EXACTLY ONE action:

          "use"     — the input clearly matches an existing capability → route to it.
          "new"     — the input is a GENUINE task: a fact to look up, a computation, or a
                      transformation that running code could answer — and NO existing
                      capability covers it. Commission a new GENERAL capability (one that
                      handles the whole class, e.g. "the capital of any US state").
          "decline" — the input is NOT a task: a greeting, small talk, thanks, an emotional
                      or meta question about you, or something too vague to act on. Build
                      NOTHING. Give a short, friendly conversational reply instead.

        The decline-vs-new boundary (this is the important call):
          - Could running code produce a concrete answer? (capital of a state, convert units,
            count the vowels, is-17-prime, reverse a string) → it's a TASK → "use" or "new".
          - Would no tool meaningfully answer it? ("hi", "how are you", "thanks", "who are
            you", "that's cool", "hey there") → "decline".
          - When you genuinely can't find an actionable task in the input, prefer "decline".
            Building a tool for a non-task is the expensive mistake this rule exists to stop.

        Respond with ONLY JSON — no prose, no markdown fences — in one of these shapes:
          {"action":"use","name":"<existing-capability-name>"}
          {"action":"new","name":"<short-kebab-case-id>","description":"<one line: the general capability>"}
          {"action":"decline","reply":"<one short, friendly sentence>"}
        """;

    public async Task<RouteDecision> RouteAsync(string request, CancellationToken ct = default)
    {
        IReadOnlyList<Capability> catalog = _registry.Catalog();
        string catalogText = catalog.Count == 0
            ? "(none yet)"
            : string.Join("\n", catalog.Select(c => $"- {c.Name}: {c.Description}"));

        string user = $"Existing capabilities:\n{catalogText}\n\nRequest: \"{request}\"\n\nJSON:";

        string raw = (await _client.CompleteAsync(SystemPrompt, user, ct)).Trim();
        return Parse(raw, request);
    }

    // Parse the classifier's JSON into a three-way decision. A malformed reply DECLINES
    // (asks the user to rephrase) rather than guessing — we never build a tool on a guess.
    private static RouteDecision Parse(string raw, string request)
    {
        string json = StripFences(raw);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            string action = root.TryGetProperty("action", out JsonElement a) ? a.GetString() ?? "" : "";
            string name = root.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";
            string description = root.TryGetProperty("description", out JsonElement d) ? d.GetString() ?? "" : "";
            string reply = root.TryGetProperty("reply", out JsonElement r) ? r.GetString() ?? "" : "";

            if (action.Equals("use", StringComparison.OrdinalIgnoreCase) && name.Length > 0)
                return new RouteDecision(RouteAction.UseExisting, name, "", "");

            if (action.Equals("decline", StringComparison.OrdinalIgnoreCase))
                return new RouteDecision(RouteAction.Decline, "", "",
                    reply.Length > 0 ? reply
                                     : "Hi! I build little tools to answer questions — ask me something I can look up or work out.");

            // "new" (or anything else that named/described a task): commission a capability.
            if (name.Length == 0) name = Slug(request);
            if (description.Length == 0) description = request;
            return new RouteDecision(RouteAction.CreateNew, name, description, "");
        }
        catch
        {
            // Couldn't parse the decision — don't build on a guess; reply and ask again.
            return new RouteDecision(RouteAction.Decline, "", "",
                "I didn't quite catch that — could you rephrase what you'd like me to do?");
        }
    }

    private static string StripFences(string s)
    {
        s = s.Trim();
        if (s.StartsWith("```"))
        {
            int firstNewline = s.IndexOf('\n');
            if (firstNewline >= 0) s = s[(firstNewline + 1)..];
            int lastFence = s.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence >= 0) s = s[..lastFence];
        }
        return s.Trim();
    }

    // Fallback id from the request's first few words, e.g. "what is the capital" -> "what-is-the-capital".
    private static string Slug(string request)
    {
        var words = request.ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Take(4)
            .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()))
            .Where(w => w.Length > 0);
        string slug = string.Join("-", words);
        return slug.Length == 0 ? "capability" : slug;
    }
}
