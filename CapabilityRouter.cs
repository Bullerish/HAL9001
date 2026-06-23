using System.Text.Json;

namespace HAL9001;

/// <summary>What the classifier decided to do with an input — the three-way decision.</summary>
public enum RouteAction
{
    UseExisting, // input clearly matches an existing capability → route to it
    CreateNew,   // a genuine task with no existing capability → commission a new general one
    Decline,     // NOT a task (greeting, chitchat, vague) → reply conversationally, build nothing
    Introspect,  // a question ABOUT THE AGENT ITSELF → answer from the self-model (its own state)
}

/// <summary>
/// The classifier's decision.
///   UseExisting → Name is the existing capability.
///   CreateNew   → Name is a proposed id, Description the one-line spec of the GENERAL capability.
///   Decline     → Reply is a short conversational response; nothing is built.
///   Introspect  → SelfTopic says which aspect of itself was asked about (answered from real state).
/// </summary>
public sealed record RouteDecision(
    RouteAction Action, string Name, string Description, string Reply,
    CapType InputType = CapType.String, CapType OutputType = CapType.String,
    StabilityKind Stability = StabilityKind.Stable,
    SelfTopic SelfTopic = SelfTopic.Identity);

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
          "self"    — the input asks ABOUT THE AGENT ITSELF: what it can do, what it knows,
                      what it has done or learned lately, how many capabilities/facts it has,
                      who/what it is, OR HOW IT IS / HOW IT FEELS (its mood). It is answered from
                      the agent's OWN state (not a tool, not a built fact). Also give a "topic" (below).
          "decline" — the input is NOT a task and NOT about the agent: a greeting, thanks, "that's
                      cool", or something too vague to act on. Build NOTHING. Give a short, friendly
                      conversational reply instead.

        The boundary (these are the important calls):
          - Could running code produce a concrete answer? (capital of a state, convert units,
            count the vowels, is-17-prime, reverse a string) → it's a TASK → "use" or "new".
          - Is it about the AGENT itself? ("what can you do", "what do you know", "who/what are
            you", "what have you learned lately", "how many capabilities do you have", "tell me
            about yourself", "how are you", "how do you feel", "what's your mood") → "self". Both
            "who are you" AND "how are you" are "self" now. A factual lookup like "what is the
            capital of Ohio" is a TASK, NOT "self".
          - Is it about the USER / their relationship with the agent? ("what do you know about me",
            "what are my interests", "do you remember me", "what have I been asking about") →
            "self" with topic "user". ("who am I" usually means this too.)
          - Would no tool meaningfully answer it and it's not about the agent? ("hi", "thanks",
            "that's cool", "hey there") → "decline".
          - When you genuinely can't find an actionable task in the input, prefer "decline".
            Building a tool for a non-task is the expensive mistake this rule exists to stop.

        For a "self" question, also give the "topic" — the closest of:
          capabilities (what it can do) | knowledge (what facts it holds) | history (what it has
          done/learned recently) | scale (how many capabilities/facts/events) | mood (how it is /
          how it feels) | user (what it knows about ME/the user, my interests) | identity (who/what
          it is, or anything general about itself).

        For a "new" capability, also declare its INPUT and OUTPUT types from this fixed set:
          String (free-form text), Int (a whole number), Number (integer or decimal),
          Bool (yes/no), Date (a calendar date).
        Pick the types the capability fundamentally works on — e.g. "is N prime" is Int -> Bool;
        "convert C to F" is Number -> Number; "capital of a state" is String -> String. These types
        make the generated handler parse its input robustly. Use String when unsure.

        Also declare its STABILITY:
          "stable" — a PURE function of its input: the same input gives the same answer forever
                     (is-28-perfect, capital-of-a-state, convert-C-to-F). Its answer is worth caching.
          "live"   — the answer depends on the CURRENT date/time, so it changes over time
                     (days-until-Christmas, what-day-is-it-today, how-old-is-someone-born-on-D).
                     Its answer must NOT be cached.

        Respond with ONLY JSON — no prose, no markdown fences — in one of these shapes:
          {"action":"use","name":"<existing-capability-name>"}
          {"action":"new","name":"<short-kebab-case-id>","description":"<one line: the general capability>","inputType":"<String|Int|Number|Bool|Date>","outputType":"<String|Int|Number|Bool|Date>","stability":"<stable|live>"}
          {"action":"self","topic":"<capabilities|knowledge|history|scale|identity>"}
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

            if (action.Equals("self", StringComparison.OrdinalIgnoreCase))
            {
                string topic = root.TryGetProperty("topic", out JsonElement tp) ? tp.GetString() ?? "" : "";
                return new RouteDecision(RouteAction.Introspect, "", "", "", SelfTopic: SelfTopics.Parse(topic));
            }

            if (action.Equals("decline", StringComparison.OrdinalIgnoreCase))
                return new RouteDecision(RouteAction.Decline, "", "",
                    reply.Length > 0 ? reply
                                     : "Hi! I build little tools to answer questions — ask me something I can look up or work out.");

            // "new" (or anything else that named/described a task): commission a capability.
            if (name.Length == 0) name = Slug(request);
            if (description.Length == 0) description = request;
            string inType = root.TryGetProperty("inputType", out JsonElement it) ? it.GetString() ?? "" : "";
            string outType = root.TryGetProperty("outputType", out JsonElement ot) ? ot.GetString() ?? "" : "";
            string stab = root.TryGetProperty("stability", out JsonElement st) ? st.GetString() ?? "" : "";
            return new RouteDecision(RouteAction.CreateNew, name, description, "",
                CapTypes.Parse(inType), CapTypes.Parse(outType), StabilityKinds.Parse(stab));
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
