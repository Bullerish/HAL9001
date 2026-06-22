using System.Text;

namespace HAL9001;

/// <summary>What aspect of itself the hive is being asked about. The router classifies a
/// self-referential question into one of these (or none → it's an ordinary task).</summary>
public enum SelfTopic
{
    Identity,      // "who/what are you?"                  → a grounded self-summary
    Capabilities,  // "what can you do?"                   → the registry, listed
    Knowledge,     // "what do you know?"                  → the facts it holds
    History,       // "what have you done/learned lately?" → recent episodic memory
    Scale,         // "how many capabilities do you have?" → the counts
}

/// <summary>Parse a router "topic" string into a <see cref="SelfTopic"/> (default Identity).</summary>
public static class SelfTopics
{
    public static SelfTopic Parse(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "capabilities" or "capability" or "skills" or "abilities" => SelfTopic.Capabilities,
        "knowledge" or "facts" or "know" => SelfTopic.Knowledge,
        "history" or "memory" or "recent" or "lately" => SelfTopic.History,
        "scale" or "count" or "counts" or "how-many" => SelfTopic.Scale,
        _ => SelfTopic.Identity,
    };
}

/// <summary>
/// THE SELF-MODEL (sentience ladder, bite 2) — the hive answering "what am I?" FROM ITS OWN STATE.
///
/// This is grounded metacognition, and it stays true to the project's first principle (the LLM is a
/// toolsmith, never the oracle): the CONTENT of every self-description is read straight from real
/// state — the capability registry, the shared facts table, and the episodic <see cref="EventLog"/>
/// (bite 1) — and rendered by code. The LLM's only job (folded into the existing router call, so no
/// extra round-trip) is to recognize that a question is about the agent itself and pick which topic.
/// It cannot invent a capability it doesn't have, because the list comes from the registry, not the
/// model.
///
/// Because facts and events live in the SHARED hive and the catalog is shared via GitHub, a node
/// answering "what can you do / what do you know / what have you done" is really describing the HIVE.
/// </summary>
public sealed class SelfModel
{
    private readonly HandlerRegistry _registry;
    private readonly TursoClient? _turso;
    private readonly EventLog _events;

    public SelfModel(HandlerRegistry registry, TursoClient? turso, EventLog events)
    {
        _registry = registry;
        _turso = turso;
        _events = events;
    }

    /// <summary>Answer a self-referential question by gathering the relevant real state and rendering
    /// it. Only the state each topic needs is fetched (introspection is rare, not the hot path).</summary>
    public async Task<string> DescribeAsync(SelfTopic topic)
    {
        return topic switch
        {
            SelfTopic.Capabilities => RenderCapabilities(),
            SelfTopic.Knowledge => RenderKnowledge(await FactsAsync()),
            SelfTopic.History => RenderHistory(await _events.RecentAsync(8)),
            SelfTopic.Scale => RenderScale(_registry.Count, await FactsAsync(), await _events.StatsAsync()),
            _ => RenderIdentity(_registry.Count, await FactsAsync(), await _events.StatsAsync(), await _events.RecentAsync(1)),
        };
    }

    // ── state gathering ──────────────────────────────────────────────────────────────────

    private sealed record FactsView(int Total, int Explicit, int Derived, List<(string Key, string Value, string Source)> Sample, bool HiveOff);

    private async Task<FactsView> FactsAsync()
    {
        if (_turso is null) return new FactsView(0, 0, 0, new(), HiveOff: true);
        List<List<string?>> rows;
        try { rows = await _turso.ExecuteAsync("SELECT key, value, source FROM facts ORDER BY updated_at DESC"); }
        catch { return new FactsView(0, 0, 0, new(), HiveOff: true); } // unreachable → behave as "no memory"

        var sample = new List<(string, string, string)>();
        int expl = 0, deriv = 0;
        foreach (var r in rows)
        {
            if (r.Count < 3 || r[0] is null) continue;
            string source = r[2] ?? "explicit";
            if (source == "derived") deriv++; else expl++;
            if (sample.Count < 10) sample.Add((r[0]!, r[1] ?? "", source));
        }
        return new FactsView(expl + deriv, expl, deriv, sample, HiveOff: false);
    }

    // ── rendering (pure functions of the gathered state) ───────────────────────────────────

    private string RenderCapabilities()
    {
        var caps = _registry.Catalog();
        if (caps.Count == 0)
            return "I can't do anything yet — I have no capabilities. Ask me to do something and I'll write the tool for it.";
        var sb = new StringBuilder();
        sb.AppendLine($"I can do {caps.Count} thing(s) — each one a tool I wrote and compiled myself:");
        foreach (Capability c in caps.OrderBy(c => c.Name))
            sb.AppendLine($"  • {c.Name} [{CapTypes.Name(c.InputType)}→{CapTypes.Name(c.OutputType)}, {StabilityKinds.Name(c.Stability)}] — {c.Description}");
        return sb.ToString().TrimEnd();
    }

    private static string RenderKnowledge(FactsView f)
    {
        if (f.HiveOff) return "I have no shared knowledge store configured, so I'm not holding any facts right now.";
        if (f.Total == 0) return "I don't know any facts yet — nothing has been remembered or worked out.";
        var sb = new StringBuilder();
        sb.AppendLine($"I know {f.Total} fact(s) — {f.Explicit} I was told, {f.Derived} I worked out for myself:");
        foreach (var (key, value, source) in f.Sample)
            sb.AppendLine($"  • {key} = {value} ({source})");
        if (f.Total > f.Sample.Count) sb.AppendLine($"  …and {f.Total - f.Sample.Count} more.");
        return sb.ToString().TrimEnd();
    }

    private static string RenderHistory(IReadOnlyList<HiveEvent> recent)
    {
        if (recent.Count == 0) return "I don't remember doing anything yet (no persistent memory, or nothing recorded).";
        var sb = new StringBuilder();
        sb.AppendLine("Lately I have:");
        // RecentAsync returns oldest-first; show newest-first for a natural "recently" reading.
        foreach (HiveEvent e in recent.Reverse())
            sb.AppendLine($"  • {e.Summary}  ({e.Kind}, {Short(e.Timestamp)}, {e.Actor})");
        return sb.ToString().TrimEnd();
    }

    private static string RenderScale(int capCount, FactsView f, EventStats s)
    {
        var sb = new StringBuilder();
        sb.Append($"In numbers: {capCount} capabilit{(capCount == 1 ? "y" : "ies")}, {f.Total} fact(s), {s.Total} recorded event(s)");
        if (s.Earliest is not null) sb.Append($" since {Short(s.Earliest)} ({Age(s.Earliest)})");
        sb.Append('.');
        if (s.ByKind.Count > 0)
            sb.Append("  Most of my life so far: " + string.Join(", ", s.ByKind.Take(4).Select(k => $"{k.Count}× {k.Kind}")) + ".");
        return sb.ToString();
    }

    private static string RenderIdentity(int capCount, FactsView f, EventStats s, IReadOnlyList<HiveEvent> newest)
    {
        var sb = new StringBuilder();
        sb.Append("I am HAL9001 — a self-extending agent: when I'm asked something I can't do, I write the code to do it, ");
        sb.Append("compile it while I run, and keep it. ");
        sb.Append($"Right now I hold {capCount} self-written capabilit{(capCount == 1 ? "y" : "ies")}");
        if (!f.HiveOff) sb.Append($" and {f.Total} fact(s) ({f.Explicit} told, {f.Derived} self-derived)");
        sb.Append('.');
        if (s.Total > 0)
        {
            sb.Append($" I remember {s.Total} event(s) of my own history");
            if (s.Earliest is not null) sb.Append($", stretching back to {Short(s.Earliest)} ({Age(s.Earliest)})");
            sb.Append('.');
        }
        if (newest.Count > 0) sb.Append($" Most recently, I {Lower(newest[^1].Summary.TrimEnd('.', ' '))}.");
        return sb.ToString();
    }

    // ── small formatting helpers ───────────────────────────────────────────────────────────

    private static string Short(string iso) => iso.Length >= 19 ? iso[..19].Replace('T', ' ') : iso;

    private static string Lower(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    // Human-readable age from an ISO timestamp ("3 days", "5 hours", "12 minutes").
    private static string Age(string iso)
    {
        if (!DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime then))
            return "a while";
        TimeSpan span = DateTime.UtcNow - then.ToUniversalTime();
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays} day(s) ago";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours} hour(s) ago";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes} minute(s) ago";
        return "moments ago";
    }
}
