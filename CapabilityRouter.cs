using System.Text.Json;

namespace HAL9001;

/// <summary>What the router decided to do with a request.</summary>
public enum RouteAction
{
    UseExisting, // an existing capability already covers this request
    CreateNew,   // no existing capability fits — commission a new, general one
}

/// <summary>
/// The router's decision. For <see cref="RouteAction.UseExisting"/>, Name is the existing
/// capability. For <see cref="RouteAction.CreateNew"/>, Name is a proposed id and
/// Description is the one-line spec of the GENERAL capability to build.
/// </summary>
public sealed record RouteDecision(RouteAction Action, string Name, string Description);

/// <summary>
/// The "recognize, don't match" step (Rung 1a). Instead of slug-matching a request's
/// literal text, it asks the LLM to map the request to the agent's catalog of capabilities
/// by MEANING — reuse one if it fits, otherwise describe a new general capability to build.
///
/// Crucially, the router NEVER answers the request. It only decides which tool is needed.
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
        You route a user request to a CAPABILITY — a reusable C# method the program will run.
        You NEVER answer the request yourself; you only decide which capability is needed.

        You are given the request and a catalog of capabilities the program already has
        (each as "name: description"). Decide exactly one of:
          - an EXISTING capability already covers this request, or
          - a NEW, GENERAL capability is needed — one that would handle the whole CLASS of
            such requests (e.g. not "capital of Missouri" but "the capital of any US state").

        Respond with ONLY JSON — no prose, no markdown fences — in one of these shapes:
          {"action":"use","name":"<existing-capability-name>"}
          {"action":"new","name":"<short-kebab-case-id>","description":"<one line: the general capability>"}
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

    // Parse the router's JSON. Anything malformed falls back to "commission a new capability
    // derived from the request" — a safe default that just generates rather than crashing.
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

            if (action.Equals("use", StringComparison.OrdinalIgnoreCase) && name.Length > 0)
                return new RouteDecision(RouteAction.UseExisting, name, "");

            // "new" (or anything else): build a general capability.
            if (name.Length == 0) name = Slug(request);
            if (description.Length == 0) description = request;
            return new RouteDecision(RouteAction.CreateNew, name, description);
        }
        catch
        {
            return new RouteDecision(RouteAction.CreateNew, Slug(request), request);
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
