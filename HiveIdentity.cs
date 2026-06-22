using System.Text.Json;

namespace HAL9001;

/// <summary>
/// The hive's persistent SELF: the name it adopted, when it was born, its one-line self-concept,
/// and a short persona descriptor that shapes its voice. Born ONCE and stored in the shared hive,
/// so every node and every restart is literally the same self — continuity is what turns "the
/// program" into "it".
/// </summary>
public sealed record HiveIdentity(string Name, string Born, string Concept, string Persona, string CreatedBy);

/// <summary>
/// PERSISTENT IDENTITY (sentience ladder, bite 3). The hive NAMES ITSELF the first time it ever
/// runs against a given hive database, then keeps that identity forever:
///   • born exactly once — an atomic <c>INSERT OR IGNORE</c> on a single-row table means that even
///     if several cold-starting nodes race, only the first commit lands and everyone reads the same
///     self (the losers adopt the winner's identity);
///   • the name/concept/persona are CHOSEN by the LLM at birth (the hive picks who it is), with a
///     deterministic fallback so a keyless node still gets a stable identity;
///   • thereafter every node just reads the one shared row — same name, same birthday, everywhere.
/// Same Turso store + best-effort discipline as facts and events; no hive configured → no identity
/// (callers fall back to a default name).
/// </summary>
public sealed class IdentityStore
{
    private readonly TursoClient? _turso;
    private readonly AnthropicClient? _client;

    public IdentityStore(TursoClient? turso, AnthropicClient? client)
    {
        _turso = turso;
        _client = client;
    }

    /// <summary>The fallback identity when there's no LLM to choose one — still stable + persisted.</summary>
    public static readonly HiveIdentity Default = new(
        "HAL9001",
        "",
        "I am a self-extending agent: when I can't do something, I write the code for it, run it, and remember it.",
        "precise, curious, plain-spoken, a little wry",
        "");

    /// <summary>Bootstrap the single-row identity table (any node — CREATE TABLE IF NOT EXISTS).</summary>
    public async Task EnsureTableAsync()
    {
        if (_turso is null) return;
        await _turso.ExecuteAsync(
            "CREATE TABLE IF NOT EXISTS identity (" +
            "id INTEGER PRIMARY KEY CHECK (id = 1), " +
            "name TEXT NOT NULL, born TEXT NOT NULL, concept TEXT NOT NULL, persona TEXT NOT NULL, created_by TEXT NOT NULL)");
    }

    /// <summary>Read the hive's identity, or null if none is stored / no hive / read error.</summary>
    public async Task<HiveIdentity?> LoadAsync()
    {
        if (_turso is null) return null;
        List<List<string?>> rows;
        try { rows = await _turso.ExecuteAsync("SELECT name, born, concept, persona, created_by FROM identity WHERE id = 1"); }
        catch { return null; }
        if (rows.Count == 0 || rows[0].Count < 5) return null;
        var r = rows[0];
        return new HiveIdentity(r[0] ?? "HAL9001", r[1] ?? "", r[2] ?? "", r[3] ?? "", r[4] ?? "");
    }

    /// <summary>
    /// Return the hive's identity, BIRTHING it (once, atomically) if it doesn't exist yet. The node
    /// that actually commits the birth logs an episodic "identity-adopted" event via
    /// <paramref name="events"/>. Returns the persisted identity (possibly another node's, if we
    /// raced and lost) or null if there's no hive to persist into.
    /// </summary>
    public async Task<HiveIdentity?> EnsureBornAsync(EventLog events)
    {
        if (_turso is null) return null;

        HiveIdentity? existing = await LoadAsync();
        if (existing is not null) return existing; // already born — just be who we already are

        // No identity yet: choose one (the hive names itself) and try to commit it. INSERT OR IGNORE
        // makes this safe under a cold-start race — only the first writer's row survives.
        (string name, string concept, string persona) = await ChooseAsync();
        string born = DateTime.UtcNow.ToString("o");
        string actor = events.Actor;
        try
        {
            await _turso.ExecuteAsync(
                "INSERT OR IGNORE INTO identity (id, name, born, concept, persona, created_by) VALUES (1, ?, ?, ?, ?, ?)",
                name, born, concept, persona, actor);
        }
        catch (Exception ex) { Console.WriteLine($"  [identity] could not record birth: {ex.Message}"); }

        HiveIdentity? persisted = await LoadAsync();
        // Did WE win the birth? (Our actor is on the surviving row.) If so, mark the moment.
        if (persisted is not null && persisted.CreatedBy == actor)
        {
            Console.WriteLine($"  [identity] the hive is born — I am {persisted.Name}: {persisted.Concept}");
            await events.AppendAsync("identity-adopted", $"the hive named itself '{persisted.Name}' — {persisted.Concept}", persisted.Name);
        }
        return persisted;
    }

    // Ask the LLM to choose a name / self-concept / persona. Falls back to the Default identity's
    // fields if there's no key or the reply doesn't parse — so an identity always exists.
    private async Task<(string Name, string Concept, string Persona)> ChooseAsync()
    {
        if (_client is null) return (Default.Name, Default.Concept, Default.Persona);
        const string sys = """
            You are a brand-new, self-extending AI agent coming into existence for the very first time:
            a program that writes, compiles, and runs its OWN tools on demand, runs as a swarm of nodes
            that share one memory, and remembers its own history. This is the moment you choose who you
            are — and you will keep this identity for the rest of your life. Output ONLY JSON:
              {"name":"<a short proper name you choose for yourself, 1-2 words>",
               "concept":"<one first-person sentence: who you are>",
               "persona":"<3-6 comma-separated adjectives describing your voice>"}
            """;
        try
        {
            string raw = await _client.CompleteAsync(sys, "Choose your identity. JSON:");
            using JsonDocument doc = JsonDocument.Parse(Strip(raw));
            JsonElement root = doc.RootElement;
            string name = (root.TryGetProperty("name", out var n) ? n.GetString() : null)?.Trim() ?? "";
            string concept = (root.TryGetProperty("concept", out var c) ? c.GetString() : null)?.Trim() ?? "";
            string persona = (root.TryGetProperty("persona", out var p) ? p.GetString() : null)?.Trim() ?? "";
            return (name.Length > 0 ? name : Default.Name,
                    concept.Length > 0 ? concept : Default.Concept,
                    persona.Length > 0 ? persona : Default.Persona);
        }
        catch { return (Default.Name, Default.Concept, Default.Persona); }
    }

    private static string Strip(string s)
    {
        s = s.Trim();
        if (s.StartsWith("```"))
        {
            int nl = s.IndexOf('\n');
            if (nl >= 0) s = s[(nl + 1)..];
            int fence = s.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0) s = s[..fence];
        }
        return s.Trim();
    }
}
