namespace HAL9001;

/// <summary>
/// One recorded moment in the hive's life — a single line of its autobiography.
/// <para><see cref="Id"/> is the monotonic row id (the timeline's order), <see cref="Timestamp"/>
/// is ISO-8601 UTC, <see cref="Actor"/> is which node did it, <see cref="Kind"/> is a short
/// machine-readable category (e.g. "capability-commissioned"), <see cref="Summary"/> is the
/// human-readable one-liner, and <see cref="Ref"/> optionally links to the thing involved
/// (a capability name, a fact key, a request id).</para>
/// </summary>
public sealed record HiveEvent(long Id, string Timestamp, string Actor, string Kind, string Summary, string? Ref);

/// <summary>Aggregate view of the event log for the self-model: how many events the hive has, the
/// earliest one (its "birth"), and a per-kind tally (most frequent first).</summary>
public sealed record EventStats(int Total, string? Earliest, List<(string Kind, int Count)> ByKind);

/// <summary>
/// EPISODIC MEMORY — the hive's autobiographical event log (sentience-ladder bite 1).
///
/// A self that has a PAST IT CAN RECALL is the substrate everything above (self-model,
/// curiosity, narrative) will query. Significant acts — a capability commissioned, a fact
/// derived, a deliberation won, a node death/recovery, a kernel winner — append one row here.
///
/// It reuses the exact discipline the facts store already established:
///   • the SAME shared Turso table for every node, so the timeline is a HIVE property
///     (events from all nodes interleave into one history) and survives restarts;
///   • credentials only from the environment (via <see cref="TursoClient"/>), never in code;
///   • writes are BEST-EFFORT — a logging hiccup is caught and never crashes the agent
///     (memory is valuable, but losing one line must not take down the thing remembering).
///
/// Without Turso configured the log is simply off (<see cref="Enabled"/> false) — a no-op,
/// exactly like the facts feature, so a keyless/hiveless node runs unchanged.
/// </summary>
public sealed class EventLog
{
    private readonly TursoClient? _turso;

    /// <summary>Who is writing events — the node id in the swarm, "single" for the lone agent,
    /// "kernel@&lt;machine&gt;" for a kernel-optimization run. Set once at startup by the host.</summary>
    public string Actor { get; set; }

    public EventLog(TursoClient? turso, string actor)
    {
        _turso = turso;
        Actor = actor;
    }

    /// <summary>Build a log straight from the env credentials (used by standalone replay /
    /// the kernel run, which don't construct an <see cref="AgentCore"/>).</summary>
    public static EventLog FromEnvironment(string actor) => new(TursoClient.FromEnvironment(), actor);

    /// <summary>True when the hive store is configured — otherwise every method is a no-op.</summary>
    public bool Enabled => _turso is not null;

    /// <summary>Bootstrap the shared events table (any node — CREATE TABLE IF NOT EXISTS).
    /// The AUTOINCREMENT id gives a strict, gap-tolerant chronological order across all nodes.</summary>
    public async Task EnsureAsync()
    {
        if (_turso is null) return;
        await _turso.ExecuteAsync(
            "CREATE TABLE IF NOT EXISTS events (" +
            "id INTEGER PRIMARY KEY AUTOINCREMENT, " +
            "ts TEXT NOT NULL, actor TEXT NOT NULL, kind TEXT NOT NULL, summary TEXT NOT NULL, ref_id TEXT)");
    }

    /// <summary>
    /// Record one significant act. Best-effort: a failure is reported once and swallowed, so the
    /// caller's real work (answering, electing, benchmarking) is never interrupted by bookkeeping.
    /// </summary>
    public async Task AppendAsync(string kind, string summary, string? refId = null)
    {
        if (_turso is null) return;
        try
        {
            await _turso.ExecuteAsync(
                "INSERT INTO events (ts, actor, kind, summary, ref_id) VALUES (?, ?, ?, ?, ?)",
                DateTime.UtcNow.ToString("o"), Actor, kind, summary, refId);
        }
        catch (Exception ex) { Console.WriteLine($"  [memory] could not record event ({kind}): {ex.Message}"); }
    }

    /// <summary>
    /// Replay the most recent <paramref name="limit"/> events in CHRONOLOGICAL order (oldest first) —
    /// the hive recalling its history. Returns an empty list with no hive / on any read error.
    /// </summary>
    public async Task<List<HiveEvent>> RecentAsync(int limit)
    {
        if (_turso is null) return new();
        int n = Math.Clamp(limit, 1, 1000);
        List<List<string?>> rows;
        // Take the newest N by id, then we reverse to oldest-first below. (n is our own validated
        // int, so inlining it is safe and sidesteps binding an integer LIMIT as text.)
        try { rows = await _turso.ExecuteAsync($"SELECT id, ts, actor, kind, summary, ref_id FROM events ORDER BY id DESC LIMIT {n}"); }
        catch { return new(); }

        var events = new List<HiveEvent>();
        foreach (var r in rows)
        {
            if (r.Count < 6 || r[0] is null) continue;
            long id = long.TryParse(r[0], out long parsed) ? parsed : 0;
            events.Add(new HiveEvent(id, r[1] ?? "", r[2] ?? "", r[3] ?? "", r[4] ?? "", r[5]));
        }
        events.Reverse(); // DESC fetch → chronological (oldest first) for replay
        return events;
    }

    /// <summary>
    /// All events of one <paramref name="kind"/>, NEWEST FIRST — for catalog-style reads (e.g. every
    /// capability ever commissioned) where recency-windowed <see cref="RecentAsync"/> could miss an
    /// early entry. Returns an empty list with no hive / on any read error.
    /// </summary>
    public async Task<List<HiveEvent>> ByKindAsync(string kind, int limit = 500)
    {
        if (_turso is null) return new();
        int n = Math.Clamp(limit, 1, 1000);
        List<List<string?>> rows;
        try { rows = await _turso.ExecuteAsync($"SELECT id, ts, actor, kind, summary, ref_id FROM events WHERE kind = ? ORDER BY id DESC LIMIT {n}", kind); }
        catch { return new(); }

        var events = new List<HiveEvent>();
        foreach (var r in rows)
        {
            if (r.Count < 6 || r[0] is null) continue;
            long id = long.TryParse(r[0], out long parsed) ? parsed : 0;
            events.Add(new HiveEvent(id, r[1] ?? "", r[2] ?? "", r[3] ?? "", r[4] ?? "", r[5]));
        }
        return events; // already newest-first
    }

    /// <summary>
    /// Aggregate stats over the whole log, for the self-model: total event count, the earliest
    /// timestamp (the hive's "birth"), and a per-kind tally (most frequent first). One GROUP BY
    /// query. Returns zeros with no hive / on any read error.
    /// </summary>
    public async Task<EventStats> StatsAsync()
    {
        if (_turso is null) return new EventStats(0, null, new());
        List<List<string?>> rows;
        try { rows = await _turso.ExecuteAsync("SELECT kind, COUNT(*), MIN(ts) FROM events GROUP BY kind"); }
        catch { return new EventStats(0, null, new()); }

        int total = 0;
        string? earliest = null;
        var byKind = new List<(string Kind, int Count)>();
        foreach (var r in rows)
        {
            if (r.Count < 3) continue;
            string kind = r[0] ?? "?";
            int count = int.TryParse(r[1], out int c) ? c : 0;
            string? min = r[2];
            total += count;
            byKind.Add((kind, count));
            // ISO-8601 timestamps sort lexicographically, so string Min is the true earliest.
            if (min is not null && (earliest is null || string.CompareOrdinal(min, earliest) < 0)) earliest = min;
        }
        byKind.Sort((a, b) => b.Count.CompareTo(a.Count));
        return new EventStats(total, earliest, byKind);
    }

    /// <summary>Print a timeline to the console — the shared "replay the timeline" formatter used by
    /// the swarm REPL's <c>timeline</c> command and the standalone <c>timeline</c> CLI mode.</summary>
    public static void Print(IReadOnlyList<HiveEvent> events)
    {
        if (events.Count == 0) { Console.WriteLine("  (no events recorded yet)"); return; }
        Console.WriteLine($"  ── hive timeline ({events.Count} most-recent event(s), oldest first) ──");
        foreach (HiveEvent e in events)
        {
            // Trim the ISO timestamp to seconds for readability (keep the date — it's autobiography).
            string ts = e.Timestamp.Length >= 19 ? e.Timestamp[..19].Replace('T', ' ') : e.Timestamp;
            string link = string.IsNullOrEmpty(e.Ref) ? "" : $"  ⟶ {e.Ref}";
            Console.WriteLine($"  [{ts}] {e.Actor}  {e.Kind}: {e.Summary}{link}");
        }
    }
}
