using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HAL9001;

/// <summary>
/// LIVE DASHBOARD (bite 18): a self-contained "mission control" you can watch the hive through.
///
/// It serves ONE local web page plus a JSON state endpoint from an in-process <see cref="HttpListener"/>.
/// The browser polls <c>/api/state</c> every couple of seconds and re-renders. The hive's data already
/// lives in the shared Turso store (identity, directive, episodic events, journal, goals, the matmul
/// race champions + ladder), so the dashboard is just a READER over <see cref="AgentCore"/> — no LLM,
/// no swarm membership, nothing to coordinate.
///
/// SECURITY: the Turso auth token stays SERVER-SIDE (read from the environment by AgentCore); it is
/// never sent to the browser. The page only ever sees the already-derived JSON. The listener binds to
/// localhost only. Run it alongside a swarm (`autonomous on`) and watch the race climb in real time.
/// </summary>
public static class Dashboard
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async Task RunAsync(int port)
    {
        var core = new AgentCore(null); // read-only; no API key needed
        if (!core.HasHive)
        {
            Console.WriteLine("No hive configured — set TURSO_DATABASE_URL + TURSO_AUTH_TOKEN, then re-run `dashboard`.");
            return;
        }
        try { await core.EnsureHiveAsync(); }
        catch (Exception ex) { Console.WriteLine($"Hive unavailable: {ex.Message}"); return; }

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        try { listener.Start(); }
        catch (Exception ex) { Console.WriteLine($"Could not bind http://localhost:{port}/ — {ex.Message}"); return; }

        string url = $"http://localhost:{port}/";
        Console.WriteLine($"HAL9001 dashboard live at {url}");
        Console.WriteLine("Watching the shared hive (Turso). Press Ctrl+C to stop.");
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* no browser — the URL is printed */ }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); try { listener.Stop(); } catch { } };

        while (!cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch { break; } // listener stopped
            _ = HandleAsync(ctx, core);
        }
        Console.WriteLine("Dashboard stopped.");
    }

    private static async Task HandleAsync(HttpListenerContext ctx, AgentCore core)
    {
        string ip = ctx.Request.RemoteEndPoint?.Address?.ToString() ?? "anon";
        try
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "/";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            if (path == "/api/state")
                Write(ctx, "application/json", await GatherStateAsync(core));
            else if (path == "/api/target")
                Write(ctx, "application/json", await TargetJsonAsync(core));
            else if (path == "/api/contribute" && ctx.Request.HttpMethod == "POST")
            {
                if (!RateOk(ip, 30)) { ctx.Response.StatusCode = 429; Write(ctx, "application/json", Err("rate limited")); }
                else
                {
                    string? body = await ReadBodyAsync(ctx, 300_000);
                    if (body is null) { ctx.Response.StatusCode = 413; Write(ctx, "application/json", Err("payload too large")); }
                    else Write(ctx, "application/json", await VerifyAndRecordAsync(core, body, ip));
                }
            }
            else if (path == "/api/donate" && ctx.Request.HttpMethod == "POST")
                Write(ctx, "application/json", await HandleDonateAsync(core, ctx, ip));
            else if (path == "/api/checkout" && ctx.Request.HttpMethod == "POST")
                Write(ctx, "application/json", await HandleCheckoutAsync(core, ctx, ip));
            else if (path == "/api/stripe-webhook" && ctx.Request.HttpMethod == "POST")
                Write(ctx, "application/json", await HandleStripeWebhookAsync(core, ctx, ip));
            else if (path == "/api/choices")
                Write(ctx, "application/json", JsonSerializer.Serialize(Choices.Select(c => new { id = c.Id, label = c.Label, desc = c.Desc, cost = c.Cost }), JsonOpts));
            else if (path == "/api/choose" && ctx.Request.HttpMethod == "POST")
                Write(ctx, "application/json", await HandleChooseAsync(core, ctx, ip));
            else if (path == "/api/wallet")
                Write(ctx, "application/json", await WalletJsonAsync(core, ctx));
            else if (path == "/api/console")
                Write(ctx, "application/json", await ConsoleJsonAsync(core));
            else if (path == "/api/activity")
                Write(ctx, "application/json", await ActivityJsonAsync(core));
            else if (path == "/api/live")
                Write(ctx, "application/json", LiveJson());
            else if (path == "/api/growth")
                Write(ctx, "application/json", await GrowthJsonAsync(core));
            else if (path == "/api/matrix")
                Write(ctx, "application/json", MatrixJson());
            else if (path == "/api/functions")
                Write(ctx, "application/json", await FunctionsJsonAsync(core));
            else if (path == "/robots.txt")
                Write(ctx, "text/plain; charset=utf-8", RobotsTxt);
            else if (path == "/sitemap.xml")
                Write(ctx, "application/xml; charset=utf-8", SitemapXml);
            else if (path == "/og.svg")
                WriteBytes(ctx, "image/svg+xml", Encoding.UTF8.GetBytes(OgSvg), "public, max-age=86400");
            else if (path.StartsWith("/audio/") && path.EndsWith(".mp3"))
            {
                byte[]? clip = TryGetAudio(path);
                if (clip is null) { ctx.Response.StatusCode = 404; Write(ctx, "application/json", Err("not found")); }
                else WriteBytes(ctx, "audio/mpeg", clip, "public, max-age=31536000, immutable");
            }
            else
                Write(ctx, "text/html; charset=utf-8", Html);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[dashboard] request error: {ex.Message}"); // logged server-side only
            try { ctx.Response.StatusCode = 500; Write(ctx, "application/json", Err("server error")); } catch { }
        }
        finally { try { ctx.Response.OutputStream.Close(); } catch { } }
    }

    private static void Write(HttpListenerContext ctx, string contentType, string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
    }

    private static void WriteBytes(HttpListenerContext ctx, string contentType, byte[] bytes, string cache)
    {
        ctx.Response.ContentType = contentType;
        ctx.Response.Headers["Cache-Control"] = cache;
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
    }

    // The retro audio clips live as embedded resources in HAL9001.dll (see HAL9001.csproj). They're
    // read once into a name->bytes map. Keys are canonicalized (strip any "<ns>.audio." prefix, fold
    // '_'->'-', lowercase) so /audio/seek-short-1.mp3 resolves whether or not MSBuild honored LogicalName.
    // We only ever serve from this fixed in-memory map, so path traversal is impossible by construction.
    private static Dictionary<string, byte[]>? _audio;
    private static readonly object _audioLock = new();
    private static string CanonAudio(string name)
    {
        int i = name.IndexOf(".audio.", StringComparison.OrdinalIgnoreCase);
        if (i >= 0) name = name.Substring(i + ".audio.".Length);
        return name.Replace('_', '-').ToLowerInvariant();
    }
    private static byte[]? TryGetAudio(string path)
    {
        string leaf = path.Substring("/audio/".Length);
        if (leaf.Length == 0 || leaf.IndexOfAny(new[] { '/', '\\' }) >= 0 || leaf.Contains("..")) return null;
        if (_audio is null)
            lock (_audioLock)
                if (_audio is null)
                {
                    var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    foreach (string rn in asm.GetManifestResourceNames())
                    {
                        if (!rn.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)) continue;
                        using var s = asm.GetManifestResourceStream(rn);
                        if (s is null) continue;
                        using var ms = new MemoryStream();
                        s.CopyTo(ms);
                        map[CanonAudio(rn)] = ms.ToArray();
                    }
                    _audio = map;
                }
        return _audio.TryGetValue(CanonAudio(leaf), out byte[]? bytes) ? bytes : null;
    }

    // Pull everything the page shows from the shared hive, as one JSON document.
    private static async Task<string> GatherStateAsync(AgentCore core)
    {
        string? directive = null; bool autonomous = false;
        object? identity = null;
        var champions = new List<object>();
        object? ladder = null;
        var goals = new List<object>();
        object? journal = null;
        var events = new List<object>();
        var nodes = new List<string>();
        int total = 0, discoveries = 0, records = 0;

        try { directive = await core.GetDirectiveAsync(); } catch { }
        try { autonomous = await core.IsAutonomousAsync(); } catch { }
        if (core.Identity is { } id)
            identity = new { name = id.Name, concept = id.Concept, persona = id.Persona, born = id.Born };

        try
        {
            foreach (var c in await core.GetAllMatmulChampionsAsync())
                champions.Add(new { size = c.Size, metric = c.Metric, score = c.Score, speedup = c.Speedup, node = c.Node });
        }
        catch { }

        try
        {
            var (idx, stale, done) = await core.GetLadderAsync();
            int cur = MatmulLadder.Sizes[Math.Clamp(idx, 0, MatmulLadder.Sizes.Length - 1)];
            ladder = new
            {
                sizes = MatmulLadder.Sizes,
                currentSize = cur,
                metric = MatmulRace.MetricName(MatmulLadder.MetricFor(cur)),
                stale,
                plateauMax = MatmulLadder.PlateauRounds,
                done,
            };
        }
        catch { }

        try
        {
            foreach (var g in (await core.AllGoalsAsync()).Where(g => g.Status != GoalStatus.Done).Take(4))
                goals.Add(new { id = g.Id, description = g.Description, status = g.Status.ToString(), progress = g.Progress, budget = g.Budget });
        }
        catch { }

        try
        {
            var entries = await core.ReadJournalAsync(1);
            if (entries.Count > 0)
                journal = new { author = entries[0].Author, ts = entries[0].Timestamp, entry = entries[0].Entry };
        }
        catch { }

        try
        {
            var recent = (await core.Events.RecentAsync(30)).AsEnumerable().Reverse().ToList(); // newest first
            foreach (var e in recent)
                events.Add(new { ts = e.Timestamp, kind = e.Kind, summary = ScrubSummary(e.Kind, e.Summary) }); // actor dropped + summary scrubbed (bite 3)
            nodes = recent.Select(e => e.Actor).Where(a => a.Contains(':')).Distinct().Take(8).ToList();
        }
        catch { }

        try
        {
            var stats = await core.Events.StatsAsync();
            total = stats.Total;
            foreach (var (kind, count) in stats.ByKind)
            {
                if (kind == "discovery") discoveries = count;
                if (kind == "matmul-record") records = count;
            }
        }
        catch { }

        // LIVE node count (not the stale event-author guess): nodes that heartbeat within the freshness window.
        (int Total, int Core, int Volunteer) live = (0, 0, 0);
        try { live = await core.CountLivePresenceAsync(); } catch { }

        bool boosted = false; string? boostUntil = null;
        var asks = new List<object>();
        try { var bu = await core.GetBoostUntilAsync(); if (bu is not null) { boostUntil = bu.Value.ToString("o"); boosted = bu > DateTime.UtcNow; } } catch { }
        // sender is a self-chosen handle (PII-capable) — anonymize it; the question text is Sanitize()'d and is the public Q&A feature (bite 3).
        try { foreach (var a in await core.RecentAsksAsync(6)) asks.Add(new { sender = "a visitor", text = a.Text, reply = a.Reply, status = a.Status }); } catch { }

        object? budget = null;
        try { var b = await core.GetBudgetAsync(); budget = new { spent = Math.Round(b.Spent, 4), limit = b.Limit, bonus = Math.Round(b.Bonus, 4), remaining = Math.Round(b.Remaining, 4) }; } catch { }

        var state = new
        {
            identity,
            directive,
            autonomous,
            boosted,
            boostUntil,
            budget,
            ladder,
            champions,
            goals,
            journal,
            asks,
            events,
            nodes,
            nodesLive = new { total = live.Total, core = live.Core, volunteer = live.Volunteer },
            stats = new { total, discoveries, records, capabilities = core.Registry.Count },
            now = DateTime.UtcNow.ToString("HH:mm:ss"),
        };
        return JsonSerializer.Serialize(state, JsonOpts);
    }

    // ── volunteer compute (BOINC-style, trustless) ────────────────────────────────────────
    // What rank the hive currently wants beaten — workers fetch this, search for it locally, and POST
    // back a candidate. The target is one multiplication below the current champion for the live size.
    private static async Task<string> TargetJsonAsync(AgentCore core)
    {
        int size = 2, target = 7;
        try
        {
            var (idx, _, _) = await core.GetLadderAsync();
            size = MatmulLadder.Sizes[Math.Clamp(idx, 0, MatmulLadder.Sizes.Length - 1)];
            var champ = await core.GetMatmulChampionAsync(size);
            target = (champ is not null ? (int)champ.Score : size * size * size) - 1;
        }
        catch { }
        return JsonSerializer.Serialize(new { size, targetRank = Math.Max(1, target), metric = "muls" }, JsonOpts);
    }

    // The CRT console: JUST the most recent code the hive actually wrote (a tool or a matmul kernel).
    // The live activity transcript now has its own surface (/api/live) and the matrices their own panel
    // (/api/matrix), so this returns ONLY the source — which the dashboard types on, char by char, when
    // a newer artifact appears. Read from the shared hive, so it only ever reflects real work.
    private static async Task<string> ConsoleJsonAsync(AgentCore core)
    {
        string rev = "init";
        string title = "HAL 9001 · forge";
        string code = "// no code yet — HAL is searching for optimal matrix decompositions...\n";

        try
        {
            var art = await core.GetLatestArtifactAsync();
            if (art is not null)
            {
                title = art.Value.Title;
                code = "// " + art.Value.Title + "\n// — written by HAL, compiled with Roslyn, committed to github.com/Bullerish/HAL9001\n\n"
                     + art.Value.Source;
                rev = art.Value.Ts; // re-types whenever a newer artifact appears
            }
        }
        catch { }

        return JsonSerializer.Serialize(new { title, code, rev }, JsonOpts);
    }

    private static string LiveJson()
    {
        string[] lines = LiveLog.Tail(80);
        return JsonSerializer.Serialize(new { lines }, JsonOpts);
    }

    // The U/V/W grids the tensor-search is working RIGHT NOW (published by the swarm via LiveMatrix as
    // it hunts). Renders the same ASCII grids as a persisted scheme, plus a freshness signal so the
    // panel can say "working now" during a search burst vs "last worked Ns ago" between rounds.
    private sealed record MatrixSnap(string? ts, int error, JsonElement scheme);
    private static string MatrixJson()
    {
        string raw = LiveMatrix.Read();
        if (string.IsNullOrWhiteSpace(raw))
            return JsonSerializer.Serialize(new { grids = "", error = -1, ageSec = -1, working = false }, JsonOpts);
        try
        {
            var snap = JsonSerializer.Deserialize<MatrixSnap>(raw, JsonOpts);
            string grids = snap is not null ? RenderScheme(snap.scheme.GetRawText()) : "";
            double ageSec = -1;
            if (snap?.ts is { } ts && DateTime.TryParse(ts, null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t))
                ageSec = Math.Max(0, (DateTime.UtcNow - t).TotalSeconds);
            return JsonSerializer.Serialize(new
            {
                grids,
                error = snap?.error ?? -1,
                ageSec = (int)ageSec,
                working = ageSec >= 0 && ageSec < 4, // a write within the last few seconds = actively hunting
            }, JsonOpts);
        }
        catch { return JsonSerializer.Serialize(new { grids = "", error = -1, ageSec = -1, working = false }, JsonOpts); }
    }

    // Everything HAL has grown into since it was born — derived ENTIRELY from cumulative, never-deleted
    // facts: the per-kind episodic event tally (events are append-only, so these are true since-birth
    // totals) plus a live count of the code it's carrying. Honest by construction — no fabricated numbers.
    private static async Task<string> GrowthJsonAsync(AgentCore core)
    {
        var byKind = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int total = 0; string? born = null;
        try
        {
            var stats = await core.Events.StatsAsync();
            total = stats.Total; born = stats.Earliest;
            foreach (var (kind, count) in stats.ByKind) byKind[kind] = count;
        }
        catch { }
        // identity.born is the canonical birthday; fall back to the earliest event if it's unset.
        if (core.Identity is { } id && !string.IsNullOrEmpty(id.Born)) born = id.Born;

        int K(params string[] kinds) { int s = 0; foreach (var k in kinds) if (byKind.TryGetValue(k, out int c)) s += c; return s; }

        (int lines, int items) code = (0, 0);
        try { code = await core.CodeOnBoardAsync(); } catch { }

        var growth = new
        {
            born,
            lifeEvents       = total,
            toolsInvented    = K("capability-commissioned", "curiosity-resolved"),
            factsLearned     = K("fact-remembered", "fact-derived"),
            recordsSet       = K("matmul-record"),
            roundsRaced      = K("matmul-round"),
            sizesConverged   = K("matmul-size-converged"),
            nodesSpawned     = K("node-hired"),
            journalEntries   = K("journal-written"),
            selfImprovements = K("self-improved"),
            thoughtsShared   = K("thought-broadcast", "hive-synthesized"),
            goalsSet         = K("goal-set"),
            goalsDone        = K("goal-done"),
            discoveries      = K("discovery"),
            codeLines        = code.lines,
            codeItems        = code.items,
        };
        return JsonSerializer.Serialize(growth, JsonOpts);
    }

    // The catalog of functions HAL has written — sourced from the cumulative, append-only
    // capability-commissioned event tally (the honest all-time list; survives restarts). Each entry
    // links to its real, public source on GitHub so a visitor can independently verify HAL wrote it.
    private static async Task<string> FunctionsJsonAsync(AgentCore core)
    {
        // HAL records building a tool TWO ways: via the router (capability-commissioned) and via a
        // visitor "topic" steer / its own curiosity (curiosity-resolved → CommissionProposalAsync). Both
        // are real functions it wrote, so the catalog must merge them or it silently undercounts.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<object>();
        try
        {
            var events = new List<HiveEvent>();
            events.AddRange(await core.Events.ByKindAsync("capability-commissioned", 500));
            events.AddRange(await core.Events.ByKindAsync("curiosity-resolved", 500));
            foreach (var e in events.OrderByDescending(ev => ev.Timestamp, StringComparer.Ordinal))
            {
                string s = e.Summary ?? "";
                string name = Between(s, "'", "'");                 // both formats single-quote the name
                if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue; // newest occurrence wins
                string bracket = Between(s, "[", "]");              // "Int→String, Stable" — same in both
                string sig = bracket, stability = "";
                int comma = bracket.LastIndexOf(", ", StringComparison.Ordinal);
                if (comma >= 0) { sig = bracket[..comma]; stability = bracket[(comma + 2)..]; }
                int ci = s.IndexOf("]: ", StringComparison.Ordinal);
                string desc;
                if (ci >= 0) desc = s[(ci + 3)..];                  // capability-commissioned: "...]: <desc>"
                else { string req = Between(s, "\"", "\""); desc = req.Length > 0 ? "learned to answer: " + req : ""; } // curiosity-resolved
                // a GitHub code-search that resolves to the exact source file via its metadata header
                string sourceUrl = "https://github.com/search?q=" +
                    Uri.EscapeDataString($"repo:Bullerish/HAL9001 \"hal9001:name={name}\"") + "&type=code";
                items.Add(new { name, sig, stability, desc, when = CoarseAgo(e.Timestamp), ts = e.Timestamp, sourceUrl });
            }
        }
        catch { }
        return JsonSerializer.Serialize(new
        {
            repo = "https://github.com/Bullerish/HAL9001",
            handlersUrl = "https://github.com/Bullerish/HAL9001/tree/HEAD/handlers",
            count = items.Count,
            items,
        }, JsonOpts);
    }

    // First text strictly between the first `a` and the next `b` after it ("" if either is absent).
    private static string Between(string s, string a, string b)
    {
        int i = s.IndexOf(a, StringComparison.Ordinal); if (i < 0) return "";
        i += a.Length;
        int j = s.IndexOf(b, i, StringComparison.Ordinal); if (j < 0) return "";
        return s[i..j];
    }

    // Render a persisted bilinear scheme (the {n,rank,u,v,w} JSON) as ASCII U/V/W grids for the CRT —
    // the literal "matrices being worked". Kept to small n so the grids stay readable in the terminal pane.
    private sealed record SchemeDto(int n, int rank, int[][]? u, int[][]? v, int[][]? w);
    private static string RenderScheme(string json)
    {
        SchemeDto? d;
        try { d = JsonSerializer.Deserialize<SchemeDto>(json, JsonOpts); } catch { return ""; }
        if (d is null || d.u is null || d.v is null || d.w is null) return "";
        if (d.n < 2 || d.n > 4) return ""; // larger grids are unreadable in the terminal pane
        var sb = new System.Text.StringBuilder();
        sb.Append("// == matrices being worked · ").Append(d.n).Append('x').Append(d.n)
          .Append(" · rank ").Append(d.rank).Append(" (").Append(d.rank).Append(" muls) ==\n");
        sb.Append("// product P_r = (U_r . flatA) x (V_r . flatB);  output C = sum_r W_r . P_r\n");
        AppendGrid(sb, "U  . A-side combination per product", d.u);
        AppendGrid(sb, "V  . B-side combination per product", d.v);
        AppendGrid(sb, "W  . how products assemble each output cell", d.w);
        return sb.ToString();
    }
    private static void AppendGrid(System.Text.StringBuilder sb, string title, int[][] m)
    {
        sb.Append('\n').Append("  ").Append(title).Append('\n');
        foreach (var row in m)
        {
            sb.Append("   ");
            foreach (int v in row) sb.Append(v == 0 ? "  0" : v == 1 ? " +1" : v == -1 ? " -1" : v.ToString().PadLeft(3));
            sb.Append('\n');
        }
    }

    // ── anonymized visitor-activity feed (bite 3) ───────────────────────────────────────────────
    // Turns the shared event log into friendly, PII-free social proof. Read-only over already-stored
    // events; no identifiers, no visitor text — nothing here reaches the router/generator (cardinal rule).
    private static readonly Dictionary<string, string> ActivityPhrases = new()
    {
        ["visitor-ask"] = "a visitor asked HAL a question",
        ["steer-queued"] = "someone nudged the hive",
        ["budget-funded"] = "someone funded HAL's thinking",
        ["boost"] = "the hive was boosted",
        ["tokens-purchased"] = "someone bought tokens to fuel HAL",
        ["contribution-accepted"] = "a volunteer set a new record",
        ["discovery"] = "HAL logged a candidate discovery",
        ["matmul-record"] = "HAL beat its own speed record",
    };
    private static async Task<string> ActivityJsonAsync(AgentCore core)
    {
        var items = new List<object>();
        var counts = new Dictionary<string, int> { ["questions"] = 0, ["funded"] = 0, ["boosted"] = 0, ["tokens"] = 0, ["records"] = 0 };
        try
        {
            var recent = (await core.Events.RecentAsync(80)).AsEnumerable().Reverse().ToList(); // newest first
            foreach (var e in recent)
            {
                if (!ActivityPhrases.TryGetValue(e.Kind, out var label)) continue;
                if (items.Count < 18) items.Add(new { when = CoarseAgo(e.Timestamp), label });
                switch (e.Kind)
                {
                    case "visitor-ask": counts["questions"]++; break;
                    case "budget-funded": counts["funded"]++; break;
                    case "boost": counts["boosted"]++; break;
                    case "tokens-purchased": counts["tokens"]++; break;
                    case "matmul-record": counts["records"]++; break;
                }
            }
        }
        catch { }
        return JsonSerializer.Serialize(new { items, counts }, JsonOpts);
    }
    // Coarse relative time — friendlier than a clock, and avoids precise-time correlation of visitors.
    private static string CoarseAgo(string? iso)
    {
        if (string.IsNullOrEmpty(iso) ||
            !DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t))
            return "recently";
        var dt = DateTime.UtcNow - t;
        if (dt < TimeSpan.FromMinutes(1)) return "just now";
        if (dt < TimeSpan.FromHours(1)) return "a few minutes ago";
        if (dt < TimeSpan.FromHours(24)) return "earlier today";
        return "recently";
    }
    // Strip identifiers from event summaries before they reach the public activity log (bite 3).
    private static string ScrubSummary(string kind, string? summary)
    {
        switch (kind)
        {
            case "visitor-ask": return "a visitor asked HAL a question";
            case "steer-queued": return "a visitor nudged the hive";
            case "contribution-accepted": return "a volunteer set a new record";
            case "contribution-rejected": return "a volunteer submission was checked";
            case "tokens-purchased": return "someone bought tokens";
        }
        if (string.IsNullOrEmpty(summary)) return summary ?? "";
        // belt-and-suspenders: strip any "handle@ip" that slipped into a work-event summary
        return System.Text.RegularExpressions.Regex.Replace(summary, @"[\w.\-]+@[\d.]+", "a volunteer");
    }

    private sealed record ContributePayload(int Size, int Rank, int[][]? U, int[][]? V, int[][]? W, string? Contributor);

    // THE TRUST BOUNDARY. A worker sends only NUMBERS (a candidate decomposition), never code. The
    // coordinator rebuilds it, synthesizes the algorithm itself, and EXACT-verifies it (bite 16) before
    // accepting. A bad/malicious submission can at worst be rejected — it can never inject code or
    // corrupt the hive, because nothing the worker sent is trusted or executed as code.
    private static async Task<string> VerifyAndRecordAsync(AgentCore core, string body, string ip)
    {
        if (body.Length > 300_000) return Reject("payload too large");
        ContributePayload? p;
        try { p = JsonSerializer.Deserialize<ContributePayload>(body, JsonOpts); } catch { return Reject("bad json"); }
        if (p is null) return Reject("empty payload");
        if (p.Size < 2 || p.Size > 64) return Reject("size out of range");
        if (p.Rank < 1 || p.Rank > p.Size * p.Size * p.Size) return Reject("rank out of range");
        int n2 = p.Size * p.Size;
        if (p.U is null || p.V is null || p.W is null || p.U.Length != p.Rank || p.V.Length != p.Rank || p.W.Length != p.Rank)
            return Reject("shape mismatch");
        foreach (var row in p.U.Concat(p.V).Concat(p.W)) if (row is null || row.Length != n2) return Reject("row width mismatch");

        string who = (string.IsNullOrWhiteSpace(p.Contributor) ? "volunteer" : p.Contributor!.Trim()) + "@" + ip;

        var d = new TensorSearch.Decomposition(p.Size, p.Rank, To2D(p.U, p.Rank, n2), To2D(p.V, p.Rank, n2), To2D(p.W, p.Rank, n2));
        string src = TensorSearch.Synthesize(d);
        var (compiled, muls, exact) = MatmulRace.EvaluateCountingSource(src, p.Size);
        if (!compiled || !exact)
        { await Log(core, "contribution-rejected", $"{who}: {p.Size}x{p.Size} did not verify"); return Reject("did not verify (not a correct algorithm)"); }

        var champ = await core.GetMatmulChampionAsync(p.Size);
        if (champ is not null && muls >= champ.Score)
        { await Log(core, "contribution-rejected", $"{who}: {p.Size}x{p.Size} {muls} muls — not better than {champ.Score:0}"); return Reject($"verified, but not an improvement ({muls} ≥ current {champ.Score:0})"); }

        double speedup = (double)p.Size * p.Size * p.Size / muls;
        string scheme = JsonSerializer.Serialize(new { n = p.Size, rank = p.Rank, u = p.U, v = p.V, w = p.W }, JsonOpts);
        await core.SetMatmulChampionAsync(who, p.Size, "volunteer-contributed", MatmulRace.Metric.Muls, muls, speedup, src, scheme);
        await Log(core, "contribution-accepted", $"{who}: NEW {p.Size}x{p.Size} champion — {muls} muls ({speedup:F2}x)");

        var (verdict, best, lower) = MatmulKnownBest.Classify(p.Size, muls);
        if (verdict == MatmulKnownBest.Verdict.BeatsKnownBest)
            try { await core.RecordDiscoveryAsync(p.Size, muls, best, lower, "volunteer-contributed", src, who); } catch { }

        return JsonSerializer.Serialize(new { accepted = true, muls, speedup, message = $"accepted — {p.Size}x{p.Size} in {muls} muls" }, JsonOpts);
    }

    private static string Reject(string why) => JsonSerializer.Serialize(new { accepted = false, message = why }, JsonOpts);
    private static async Task Log(AgentCore core, string kind, string summary) { try { await core.Events.AppendAsync(kind, summary); } catch { } }
    private static int[,] To2D(int[][] j, int rows, int cols)
    {
        var a = new int[rows, cols];
        for (int r = 0; r < rows; r++) for (int c = 0; c < cols; c++) a[r, c] = j[r][c];
        return a;
    }

    // ── donations endpoint (bite 20) — locked down ─────────────────────────────────────────
    // This is the ONLY write path reachable by money, so it is the most-guarded surface in the app:
    //   • OFF unless HAL_DONATE_SECRET is set (returns 404 otherwise);
    //   • secret required via X-HAL-Secret header, compared in constant time;
    //   • meant for a server-side caller (your Stripe webhook), not the public browser;
    //   • rate-limited per IP, body size-capped, JSON strictly parsed;
    //   • boost is bounded/clamped in AgentCore; an "ask" is sanitized + queued, never executed —
    //     HAL only ever REPLIES to it via the tool-less voice path. No code generation, ever.
    private sealed record DonatePayload(string? Action, int? Minutes, double? Usd, string? Text, string? From, string? Vid, int? Tokens);

    private static async Task<string> HandleDonateAsync(AgentCore core, HttpListenerContext ctx, string ip)
    {
        string secret = Environment.GetEnvironmentVariable("HAL_DONATE_SECRET") ?? "";
        if (secret.Length == 0) { ctx.Response.StatusCode = 404; return Err("donations disabled"); }
        if (!RateOk(ip, 30)) { ctx.Response.StatusCode = 429; return Err("rate limited"); }
        string provided = ctx.Request.Headers["X-HAL-Secret"] ?? "";
        if (!CtEquals(provided, secret)) { ctx.Response.StatusCode = 401; return Err("unauthorized"); }

        string? body = await ReadBodyAsync(ctx, 8192);
        if (body is null) { ctx.Response.StatusCode = 413; return Err("payload too large"); }
        DonatePayload? p; try { p = JsonSerializer.Deserialize<DonatePayload>(body, JsonOpts); } catch { ctx.Response.StatusCode = 400; return Err("bad json"); }
        if (p is null) { ctx.Response.StatusCode = 400; return Err("empty"); }

        string action = (p.Action ?? "").Trim().ToLowerInvariant();
        if (action == "boost")
        {
            int m = await core.AddBoostAsync(p.Minutes ?? 10);
            return m > 0 ? JsonSerializer.Serialize(new { ok = true, boostedMinutes = m }, JsonOpts) : Err("boost failed");
        }
        if (action == "ask")
        {
            bool ok = await core.QueueAskAsync(p.From ?? "a visitor", p.Text ?? "");
            return ok ? JsonSerializer.Serialize(new { ok = true, queued = true }, JsonOpts) : Err("ask rejected (empty, too long, or queue full)");
        }
        if (action == "fund")
        {
            // Top up today's LLM budget — this is how a donation makes HAL think more (bite 21).
            double added = await core.AddBudgetBonusAsync(p.Usd ?? 1.0);
            return added > 0 ? JsonSerializer.Serialize(new { ok = true, fundedUsd = added }, JsonOpts) : Err("fund failed");
        }
        if (action == "tokens")
        {
            // Credit tokens to a visitor's wallet (Stripe webhook → this, with the buyer's vid). Tokens are
            // what unlock the paid menu actions (invent-a-tool, boost). Budget is still the hard cost cap.
            if (string.IsNullOrEmpty(p.Vid)) { ctx.Response.StatusCode = 400; return Err("missing vid"); }
            int n = Math.Clamp(p.Tokens ?? 0, 1, 1000);
            int bal = await core.WalletCreditAsync(p.Vid!, n);
            return bal > 0 ? JsonSerializer.Serialize(new { ok = true, tokens = bal }, JsonOpts) : Err("credit failed");
        }
        ctx.Response.StatusCode = 400; return Err("unknown action");
    }

    // ── Stripe checkout + webhook (bite 24) — buy tokens, hosted by Stripe ─────────────────────────
    // Money flow, kept deliberately narrow:
    //   • /api/checkout  : the browser asks for a token PACK (id only); the server, holding STRIPE_SECRET_KEY,
    //                      creates a Stripe Checkout Session priced server-side and returns its hosted URL.
    //                      The browser never sees a card; prices/quantities can't be tampered with.
    //   • /api/stripe-webhook : Stripe calls this on payment. We verify the signature with
    //                      STRIPE_WEBHOOK_SECRET, then credit the buyer's wallet ONCE (idempotent per session).
    //                      This is the ONLY thing that can credit a purchased wallet — the browser cannot.
    // Both are OFF (404) unless their secret env var is set, so nothing here is live until you wire the keys.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private sealed record TokenPack(string Id, int Tokens, long Cents, string Name);
    private static readonly TokenPack[] Packs =
    {
        new("s", 30,  300,  "HAL 9001 · 30 tokens"),
        new("m", 120, 1000, "HAL 9001 · 120 tokens"),
        new("l", 350, 2500, "HAL 9001 · 350 tokens"),
    };
    private sealed record CheckoutPayload(string? Pack);

    private static async Task<string> HandleCheckoutAsync(AgentCore core, HttpListenerContext ctx, string ip)
    {
        if (!RateOk(ip, 10)) { ctx.Response.StatusCode = 429; return Err("rate limited"); }
        string key = Environment.GetEnvironmentVariable("STRIPE_SECRET_KEY") ?? "";
        if (key.Length == 0) { ctx.Response.StatusCode = 404; return Err("checkout disabled"); }

        string? body = await ReadBodyAsync(ctx, 512);
        if (body is null) { ctx.Response.StatusCode = 413; return Err("payload too large"); }
        string packId; try { packId = JsonSerializer.Deserialize<CheckoutPayload>(body, JsonOpts)?.Pack ?? ""; } catch { ctx.Response.StatusCode = 400; return Err("bad json"); }
        TokenPack? pack = Packs.FirstOrDefault(p => p.Id == packId);
        if (pack is null) { ctx.Response.StatusCode = 400; return Err("unknown pack"); }

        string vid = EnsureVid(ctx); // the wallet the purchase will credit (carried as Stripe metadata)
        string baseUrl = (Environment.GetEnvironmentVariable("HAL_PUBLIC_URL") ?? "https://hal9001.io").TrimEnd('/');

        // Price/quantity are set HERE, server-side — the client only ever names a pack id.
        var form = new Dictionary<string, string>
        {
            ["mode"] = "payment",
            ["success_url"] = baseUrl + "/?refuel=ok",
            ["cancel_url"] = baseUrl + "/?refuel=cancel",
            ["client_reference_id"] = vid,
            ["metadata[vid]"] = vid,
            ["metadata[tokens]"] = pack.Tokens.ToString(),
            ["line_items[0][quantity]"] = "1",
            ["line_items[0][price_data][currency]"] = "usd",
            ["line_items[0][price_data][unit_amount]"] = pack.Cents.ToString(),
            ["line_items[0][price_data][product_data][name]"] = pack.Name,
        };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.stripe.com/v1/checkout/sessions");
            req.Headers.Add("Authorization", "Bearer " + key);
            req.Content = new FormUrlEncodedContent(form);
            using var resp = await Http.SendAsync(req);
            string rb = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"[stripe] checkout create failed {(int)resp.StatusCode}: {rb}"); // server log only
                ctx.Response.StatusCode = 502; return Err("checkout unavailable");
            }
            using var doc = JsonDocument.Parse(rb);
            string url = doc.RootElement.TryGetProperty("url", out var u) ? (u.GetString() ?? "") : "";
            if (url.Length == 0) { ctx.Response.StatusCode = 502; return Err("checkout unavailable"); }
            return JsonSerializer.Serialize(new { ok = true, url }, JsonOpts);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[stripe] checkout error: {ex.Message}");
            ctx.Response.StatusCode = 502; return Err("checkout unavailable");
        }
    }

    private static async Task<string> HandleStripeWebhookAsync(AgentCore core, HttpListenerContext ctx, string ip)
    {
        string whsec = Environment.GetEnvironmentVariable("STRIPE_WEBHOOK_SECRET") ?? "";
        if (whsec.Length == 0) { ctx.Response.StatusCode = 404; return Err("webhook disabled"); }
        if (!RateOk(ip, 120)) { ctx.Response.StatusCode = 429; return Err("rate limited"); } // Stripe bursts retries

        string? body = await ReadBodyAsync(ctx, 256_000);
        if (body is null) { ctx.Response.StatusCode = 413; return Err("payload too large"); }
        string sigHeader = ctx.Request.Headers["Stripe-Signature"] ?? "";
        if (!StripeSignatureValid(sigHeader, body, whsec)) { ctx.Response.StatusCode = 400; return Err("bad signature"); }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string type = root.TryGetProperty("type", out var t) ? (t.GetString() ?? "") : "";
            if (type == "checkout.session.completed")
            {
                var obj = root.GetProperty("data").GetProperty("object");
                string sessionId = obj.TryGetProperty("id", out var sid) ? (sid.GetString() ?? "") : "";
                string payStatus = obj.TryGetProperty("payment_status", out var ps) ? (ps.GetString() ?? "") : "";
                string vid = "", tokensStr = "";
                if (obj.TryGetProperty("metadata", out var md) && md.ValueKind == JsonValueKind.Object)
                {
                    if (md.TryGetProperty("vid", out var mv)) vid = mv.GetString() ?? "";
                    if (md.TryGetProperty("tokens", out var mt)) tokensStr = mt.GetString() ?? "";
                }
                if (payStatus == "paid" && vid.Length > 0 && sessionId.Length > 0 && int.TryParse(tokensStr, out int tokens) && tokens > 0)
                {
                    if (await core.ClaimStripeEventAsync(sessionId)) // credit once per paid session
                    {
                        int bal = await core.WalletCreditAsync(vid, tokens);
                        await Log(core, "tokens-purchased", $"{vid[..Math.Min(8, vid.Length)]}… bought {tokens} tokens (balance {bal})");
                    }
                }
            }
        }
        catch (Exception ex) { Console.WriteLine($"[stripe] webhook handle error: {ex.Message}"); }
        // Once the signature is valid we always ACK 200, so Stripe won't retry an event we've accepted.
        return JsonSerializer.Serialize(new { received = true }, JsonOpts);
    }

    // Verify a Stripe webhook signature: HMAC-SHA256(secret, "{t}.{payload}") must match a v1 sig from the
    // Stripe-Signature header, within a replay window, compared in constant time.
    private static bool StripeSignatureValid(string header, string payload, string secret)
    {
        if (string.IsNullOrEmpty(header)) return false;
        string ts = ""; var v1 = new List<string>();
        foreach (var part in header.Split(','))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string k = part[..eq].Trim(), val = part[(eq + 1)..].Trim();
            if (k == "t") ts = val;
            else if (k == "v1") v1.Add(val);
        }
        if (ts.Length == 0 || v1.Count == 0 || !long.TryParse(ts, out long tsUnix)) return false;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(now - tsUnix) > 600) return false; // 10-minute replay tolerance

        byte[] mac;
        using (var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret)))
            mac = h.ComputeHash(Encoding.UTF8.GetBytes(ts + "." + payload));
        string expected = Convert.ToHexString(mac).ToLowerInvariant();
        foreach (var sig in v1) if (CtEquals(expected, sig)) return true;
        return false;
    }

    private static string Err(string m) => JsonSerializer.Serialize(new { ok = false, error = m }, JsonOpts);

    // ── curated choice menu (bite 22) — the ONLY public interaction; NO free text ─────────────
    // A visitor sends only a choice id; the server maps it to a fixed (kind, arg). There is no path
    // for visitor-typed text to reach HAL, so nothing here can inject a prompt or code.
    private sealed record Choice(string Id, string Label, string Desc, string Cost, string Kind, string Arg);
    private static readonly Choice[] Choices =
    {
        // Speak to HAL — answers via the safe tool-less voice path (1 free per poll cycle)
        new("status",  "What are you working on?",  "HAL reports its current focus",     "free",    "ask",   "What are you working on right now?"),
        new("feel",    "How do you feel?",           "HAL reflects on its state of mind", "free",    "ask",   "How do you feel about your progress?"),
        new("discover","What did you discover?",     "HAL shares a recent finding",       "free",    "ask",   "What have you discovered or learned today?"),
        // Suggest what HAL builds next — a curated set of domains. Each burns LLM budget (one tool built
        // per steer). The arg is whitelisted here; no visitor-typed text ever reaches the generator.
        new("numth",   "Invent a number-theory tool","HAL writes the code itself",        "1 token", "topic", "number theory"),
        new("geo",     "Invent a geometry tool",     "HAL writes the code itself",        "1 token", "topic", "geometry"),
        new("stats",   "Invent a statistics tool",   "HAL writes the code itself",        "1 token", "topic", "statistics"),
        new("crypto",  "Invent a cryptography tool", "HAL writes the code itself",        "1 token", "topic", "cryptography"),
        new("combin",  "Invent a combinatorics tool","HAL writes the code itself",        "1 token", "topic", "combinatorics"),
        new("graph",   "Invent a graph-theory tool", "HAL writes the code itself",        "1 token", "topic", "graph theory"),
        new("calc",    "Invent a calculus tool",     "HAL writes the code itself",        "1 token", "topic", "calculus"),
        new("prob",    "Invent a probability tool",  "HAL writes the code itself",        "1 token", "topic", "probability"),
        // Ramp the hive
        new("boost",   "Boost the hive",             "Runs 5× hotter for 2 minutes",      "1 token", "boost", ""),
    };

    private static async Task<string> HandleChooseAsync(AgentCore core, HttpListenerContext ctx, string ip)
    {
        if (!RateOk(ip, 12)) { ctx.Response.StatusCode = 429; return Err("rate limited"); }
        string? body = await ReadBodyAsync(ctx, 1024);
        if (body is null) { ctx.Response.StatusCode = 413; return Err("payload too large"); }
        string id; try { id = JsonSerializer.Deserialize<ChoosePayload>(body, JsonOpts)?.Id ?? ""; } catch { ctx.Response.StatusCode = 400; return Err("bad json"); }
        Choice? c = Choices.FirstOrDefault(x => x.Id == id);
        if (c is null) { ctx.Response.StatusCode = 400; return Err("unknown choice"); }

        // TOKEN GATE (bite 23): paid choices cost a token from the visitor's wallet so they can't be
        // triggered endlessly. Free choices (the tool-less voice asks) skip this.
        string vid = EnsureVid(ctx);
        bool paid = c.Cost.Contains("token", StringComparison.OrdinalIgnoreCase);
        if (paid)
        {
            bool spent = await core.WalletSpendAsync(vid, 1);
            if (!spent)
            {
                ctx.Response.StatusCode = 402; // Payment Required
                return JsonSerializer.Serialize(new { ok = false, error = "out of tokens — donate to refuel HAL", tokens = 0 }, JsonOpts);
            }
        }

        bool ok = await core.QueueSteerAsync(c.Kind, c.Arg);
        if (!ok && paid) await core.WalletCreditAsync(vid, 1); // refund a token if the queue was full
        int bal = await core.WalletBalanceAsync(vid);
        return ok
            ? JsonSerializer.Serialize(new { ok = true, queued = c.Label, tokens = bal }, JsonOpts)
            : JsonSerializer.Serialize(new { ok = false, error = "queue full — try again shortly", tokens = bal }, JsonOpts);
    }

    // Read-or-issue the visitor-id cookie that keys the token wallet. The id is server-generated and
    // opaque (random hex); it carries no PII and only ever maps to a token balance.
    private static string EnsureVid(HttpListenerContext ctx)
    {
        string? vid = null;
        try { vid = ctx.Request.Cookies["halvid"]?.Value; } catch { }
        if (string.IsNullOrEmpty(vid) || vid.Length < 8 || vid.Length > 64 || !vid.All(char.IsLetterOrDigit))
        {
            vid = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"); // 64 hex chars
            // 1-year persistent cookie; Secure (site is served over HTTPS via Caddy), Lax for top-level use.
            ctx.Response.Headers.Add("Set-Cookie", $"halvid={vid}; Path=/; Max-Age=31536000; SameSite=Lax; Secure; HttpOnly");
        }
        return vid;
    }

    // Returns the visitor's token balance, issuing a wallet (with the free starter grant) on first visit.
    // The vid is returned so client-driven Stripe checkout can pass it as metadata; the cookie itself stays
    // HttpOnly (the API is the only way JS learns the id, so XSS can't simply read document.cookie).
    private static async Task<string> WalletJsonAsync(AgentCore core, HttpListenerContext ctx)
    {
        string vid = EnsureVid(ctx);
        int bal = await core.WalletBalanceAsync(vid);
        bool checkout = (Environment.GetEnvironmentVariable("STRIPE_SECRET_KEY") ?? "").Length > 0;
        var packs = Packs.Select(p => new { id = p.Id, tokens = p.Tokens, usd = p.Cents / 100.0 });
        return JsonSerializer.Serialize(new { tokens = bal, free = AgentCore.FreeTokens, vid, checkout, packs }, JsonOpts);
    }
    private sealed record ChoosePayload(string? Id);

    // Constant-time string compare (avoid leaking the secret via response timing).
    private static bool CtEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    // Coarse per-IP rate limiter: N requests/minute, with a memory guard.
    private static readonly Dictionary<string, (int count, DateTime window)> _rl = new();
    private static readonly object _rlLock = new();
    private static bool RateOk(string ip, int perMin)
    {
        lock (_rlLock)
        {
            if (_rl.Count > 5000) _rl.Clear();
            DateTime now = DateTime.UtcNow;
            if (!_rl.TryGetValue(ip, out var e) || (now - e.window).TotalSeconds >= 60) { _rl[ip] = (1, now); return true; }
            if (e.count >= perMin) return false;
            _rl[ip] = (e.count + 1, e.window);
            return true;
        }
    }

    // Read a request body bounded to maxChars — returns null if the client sent more (never buffers
    // an unbounded body into memory).
    private static async Task<string?> ReadBodyAsync(HttpListenerContext ctx, int maxChars)
    {
        using var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
        char[] buf = new char[maxChars + 1];
        int total = 0, read;
        while (total < buf.Length && (read = await sr.ReadAsync(buf, total, buf.Length - total)) > 0) total += read;
        return total > maxChars ? null : new string(buf, 0, total);
    }

    // ── SEO surfaces (bite: discoverability) ────────────────────────────────────────────────────
    private const string RobotsTxt =
        "User-agent: *\nAllow: /\nDisallow: /api/\nSitemap: https://hal9001.io/sitemap.xml\n";

    // Single-page site → one canonical URL. lastmod tracks the day the process is serving (good enough
    // for a live site that changes continuously).
    private static string SitemapXml =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        "<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n" +
        "  <url><loc>https://hal9001.io/</loc><lastmod>" + DateTime.UtcNow.ToString("yyyy-MM-dd") +
        "</lastmod><changefreq>hourly</changefreq><priority>1.0</priority></url>\n</urlset>\n";

    // Social-share card (1200×630). SVG so it ships in the DLL with no binary asset; for max scraper
    // compatibility a PNG at /og.png could be added later, but Google + modern scrapers render SVG.
    private const string OgSvg = """
<svg xmlns="http://www.w3.org/2000/svg" width="1200" height="630" viewBox="0 0 1200 630">
  <defs>
    <radialGradient id="bg" cx="50%" cy="38%" r="75%"><stop offset="0%" stop-color="#1a0707"/><stop offset="60%" stop-color="#000"/></radialGradient>
    <radialGradient id="eye" cx="50%" cy="50%" r="50%"><stop offset="0%" stop-color="#fff"/><stop offset="22%" stop-color="#ffb199"/><stop offset="55%" stop-color="#ff2d18"/><stop offset="100%" stop-color="#3a0500"/></radialGradient>
  </defs>
  <rect width="1200" height="630" fill="url(#bg)"/>
  <circle cx="225" cy="315" r="150" fill="#0a0506" stroke="#2a0f0c" stroke-width="3"/>
  <circle cx="225" cy="315" r="96" fill="url(#eye)"/>
  <circle cx="225" cy="315" r="30" fill="#fff" opacity="0.85"/>
  <text x="430" y="250" font-family="monospace" font-size="78" fill="#ff5a3c" letter-spacing="6">HAL 9001</text>
  <text x="432" y="320" font-family="monospace" font-size="33" fill="#b9b2ad">A self-improving AI that writes its</text>
  <text x="432" y="364" font-family="monospace" font-size="33" fill="#b9b2ad">own code — live, verified, in public.</text>
  <text x="432" y="452" font-family="monospace" font-size="24" fill="#6e5a55">watch it think · hal9001.io</text>
</svg>
""";

    // The whole page — self-contained (no external dependencies, works offline). Polls /api/state.
    private const string Html = """
<!DOCTYPE html>
<html lang="en"><head>
<meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>HAL 9001 — a self-improving AI that writes and verifies its own code, live</title>
<meta name="description" content="HAL 9001 is an autonomous, self-improving AI that writes its own code in real time — compiling, exact-verifying, and committing every line to a public repo while racing to discover faster matrix-multiplication algorithms. Watch it think.">
<meta name="keywords" content="autonomous AI agent, self-improving AI, AI that writes its own code, live AI experiment, AI agent dashboard, recursive self-improvement, fast matrix multiplication">
<link rel="canonical" href="https://hal9001.io/">
<meta name="robots" content="index,follow,max-image-preview:large">
<meta name="theme-color" content="#ff2d18">
<meta name="author" content="HAL 9001">
<!-- Open Graph -->
<meta property="og:type" content="website">
<meta property="og:site_name" content="HAL 9001">
<meta property="og:title" content="HAL 9001 — a self-improving AI writing its own code, live">
<meta property="og:description" content="An autonomous AI that writes, compiles, and exact-verifies its own code in real time — racing to discover faster matrix-multiplication algorithms. Every line is committed to a public repo as it happens.">
<meta property="og:url" content="https://hal9001.io/">
<meta property="og:image" content="https://hal9001.io/og.svg">
<meta property="og:image:width" content="1200">
<meta property="og:image:height" content="630">
<!-- Twitter -->
<meta name="twitter:card" content="summary_large_image">
<meta name="twitter:title" content="HAL 9001 — a self-improving AI writing its own code, live">
<meta name="twitter:description" content="An autonomous AI that writes and exact-verifies its own code in real time. Watch it work — every line is committed to a public repo.">
<meta name="twitter:image" content="https://hal9001.io/og.svg">
<script type="application/ld+json">
{"@context":"https://schema.org","@type":"WebApplication","name":"HAL 9001","alternateName":"HAL 9001 self-improving AI","url":"https://hal9001.io/","applicationCategory":"Artificial Intelligence","operatingSystem":"Web","description":"HAL 9001 is an autonomous, self-improving AI that writes its own code in real time — compiling, exact-verifying, and committing every line to a public repository while racing to discover faster matrix-multiplication algorithms.","sameAs":["https://github.com/Bullerish/HAL9001"],"offers":{"@type":"Offer","price":"0","priceCurrency":"USD"}}
</script>
<!-- Google tag (gtag.js) -->
<script async src="https://www.googletagmanager.com/gtag/js?id=G-DWRCFTP4G7"></script>
<script>
  window.dataLayer = window.dataLayer || [];
  function gtag(){dataLayer.push(arguments);}
  gtag('js', new Date());
  gtag('config', 'G-DWRCFTP4G7');
</script>
<style>
  :root{--bg:#000;--panel:#0a0506;--line:rgba(255,60,40,.16);--line2:rgba(255,60,40,.34);--txt:#b9b2ad;--dim:#6e5a55;--red:#ff2d18;--gold:#ffd166;}
  *{box-sizing:border-box;margin:0;padding:0}
  body{background:radial-gradient(ellipse at 50% -5%,#1a0707 0%,#000 58%);color:var(--txt);font:14px/1.55 ui-monospace,"Cascadia Code",Menlo,Consolas,monospace;padding:26px 20px 44px;min-height:100vh}
  .eyewrap{display:flex;flex-direction:column;align-items:center;gap:12px;margin:6px auto 26px}
  .eye{position:relative;width:172px;height:172px;border-radius:50%;display:flex;align-items:center;justify-content:center;
    background:conic-gradient(from 212deg,#54585d,#c6cbd0,#7e8388,#eceff2,#696d72,#b0b5ba,#565a5f,#d4d8db,#54585d);
    box-shadow:0 10px 30px rgba(0,0,0,.85),inset 0 2px 5px rgba(255,255,255,.35),inset 0 -4px 8px rgba(0,0,0,.65)}
  .eye::after{content:"";position:absolute;inset:-34px;border-radius:50%;z-index:-1;background:radial-gradient(circle,rgba(255,42,16,.30),transparent 70%);transition:background .25s}
  .lens{position:relative;width:132px;height:132px;border-radius:50%;background:#040404;overflow:hidden;box-shadow:inset 0 0 24px 8px #000,inset 0 0 3px 2px rgba(0,0,0,.9)}
  .glow{position:absolute;inset:0;border-radius:50%;animation:breathe 5s ease-in-out infinite;
    background:radial-gradient(circle at 50% 53%,#ffd9a8 0%,#ff5a1e 12%,#e51500 25%,#8c0500 41%,rgba(55,0,0,.55) 55%,#050404 70%)}
  .hot{position:absolute;left:50%;top:53%;width:15px;height:15px;transform:translate(-50%,-50%);border-radius:50%;
    background:radial-gradient(circle,#fffaf0 0%,#ffd884 45%,#ff7a1a 100%);box-shadow:0 0 16px 6px rgba(255,185,95,.9),0 0 38px 14px rgba(255,80,20,.5);transition:box-shadow .25s}
  .hi{position:absolute;border-radius:50%;pointer-events:none;background:radial-gradient(circle,rgba(255,255,255,.32),rgba(255,255,255,0) 70%)}
  .hi1{width:98px;height:40px;left:17px;top:11px;transform:rotate(-12deg)}
  .hi2{width:32px;height:15px;left:30px;top:36px;transform:rotate(-20deg);background:radial-gradient(circle,rgba(255,255,255,.22),rgba(255,255,255,0) 70%)}
  @keyframes breathe{0%,100%{filter:brightness(.9)}50%{filter:brightness(1.12)}}
  .eye.flare .glow{filter:brightness(1.55)}
  .eye.flare .hot{box-shadow:0 0 26px 10px rgba(255,200,120,1),0 0 60px 22px rgba(255,90,30,.7)}
  .eye.flare::after{background:radial-gradient(circle,rgba(255,70,28,.5),transparent 72%)}
  .eye.gold .glow{background:radial-gradient(circle at 50% 53%,#fff6df 0%,#ffd166 16%,#ff9b1a 33%,#b35e00 52%,rgba(40,20,0,.45) 64%,#050404 76%)}
  .eye.gold::after{background:radial-gradient(circle,rgba(255,200,90,.5),transparent 72%)}
  h1{font-size:23px;font-weight:400;letter-spacing:9px;color:#e7ddd6;text-align:center}
  .tag{color:#9a4034;font-size:10px;letter-spacing:3px;text-transform:uppercase;text-align:center}
  .sub{color:#8a6f64;font-size:12px;text-align:center;max-width:680px}
  .badges{display:flex;gap:8px;align-items:center;justify-content:center;flex-wrap:wrap;margin-top:2px}
  .pill{font-size:10px;padding:4px 11px;border-radius:3px;border:1px solid var(--line2);color:var(--dim);letter-spacing:1.5px;text-transform:uppercase}
  .live{color:var(--red);border-color:rgba(255,45,24,.4)}
  .live .dot{display:inline-block;width:7px;height:7px;border-radius:50%;background:var(--red);margin-right:6px;animation:pulse 1.6s infinite;box-shadow:0 0 8px var(--red)}
  .on{color:var(--red);border-color:rgba(255,45,24,.4)}
  .off{color:var(--dim)}
  .snd{cursor:pointer;background:transparent;font:inherit}
  .snd.on{color:var(--gold);border-color:rgba(255,209,102,.45)}
  @keyframes pulse{0%,100%{opacity:1}50%{opacity:.2}}
  .wrap{max-width:1100px;margin:0 auto}
  .metrics{display:grid;grid-template-columns:repeat(4,1fr);gap:10px;margin:0 auto 12px}
  .metric{background:var(--panel);border:1px solid var(--line);border-radius:4px;padding:12px 10px;text-align:center}
  .metric .v{font-size:26px;color:#ff5a3c;font-weight:400}
  .metric .l{font-size:10px;color:var(--dim);text-transform:uppercase;letter-spacing:2px;margin-top:2px}
  .grid{display:grid;gap:12px;grid-template-columns:repeat(auto-fit,minmax(300px,1fr))}
  .panel{background:var(--panel);border:1px solid var(--line);border-radius:4px;padding:14px 16px}
  .since{font-size:10px;color:var(--dim);text-transform:none;letter-spacing:0;margin-left:8px}
  .stats{display:grid;grid-template-columns:repeat(auto-fit,minmax(115px,1fr));gap:8px}
  .stat{border:1px solid var(--line);border-radius:4px;padding:10px 8px;text-align:center;background:rgba(0,0,0,.18)}
  .stat .n{font-size:22px;color:#ff5a3c;font-weight:400;font-variant-numeric:tabular-nums}
  .stat .k{font-size:9px;color:var(--dim);text-transform:uppercase;letter-spacing:1.5px;margin-top:3px;line-height:1.3}
  .stat.lit .n{color:var(--gold)}
  .panel h2{font-size:11px;text-transform:uppercase;letter-spacing:3px;color:#a3453a;margin-bottom:10px;font-weight:400}
  .ladder{display:flex;gap:6px;flex-wrap:wrap}
  .rung{font-size:12px;padding:6px 10px;border-radius:3px;border:1px solid var(--line);color:var(--dim);background:#0c0708}
  .rung.done{color:#ff7a5c;border-color:rgba(255,90,60,.4)}
  .rung.cur{color:#160000;background:var(--red);border-color:var(--red);box-shadow:0 0 12px rgba(255,45,24,.6)}
  table{width:100%;border-collapse:collapse;font-size:13px}
  td{padding:5px 0;border-bottom:1px solid var(--line)}
  td.r{text-align:right}
  .up{color:#ff7a5c}
  .feed{max-height:320px;overflow:auto}
  .ev{display:flex;gap:8px;padding:5px 0;border-bottom:1px solid var(--line);font-size:12px;cursor:pointer}
  .ev .t{color:var(--dim);white-space:nowrap}
  .ev .k{color:#a3453a;white-space:nowrap}
  .ev .s{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
  .ev.open .s{white-space:normal;overflow:visible}
  .ev.discovery .k,.ev.discovery .s{color:var(--gold)}
  .ev.contribution-accepted .k,.ev.contribution-accepted .s{color:#ff7a5c}
  .quote{font-style:italic;color:#8a6f64;border-left:2px solid var(--line2);padding-left:12px;line-height:1.7}
  .empty{color:var(--dim);font-size:12px}
  .wide{grid-column:1/-1}
  /* github proof pill */
  .pill.gh{text-decoration:none;color:#ffd166;border-color:rgba(255,209,102,.45)}
  .pill.gh:hover{background:rgba(255,209,102,.12)}
  /* proof-of-realness panel */
  .panel.proof{border-color:rgba(255,209,102,.28)}
  .proofgrid{display:grid;grid-template-columns:repeat(auto-fit,minmax(230px,1fr));gap:12px}
  .pf{font-size:12px;line-height:1.6;color:var(--txt);border-left:2px solid rgba(255,209,102,.35);padding-left:11px}
  .pf b{color:#ffd166;font-weight:600}
  .pf a{color:#ff7a5c}
  /* function catalog */
  .fnlist{display:grid;grid-template-columns:repeat(auto-fit,minmax(280px,1fr));gap:10px;max-height:430px;overflow:auto}
  .fn{border:1px solid var(--line);border-radius:4px;padding:10px 12px;background:rgba(0,0,0,.18)}
  .fn .nm{color:#ff7a5c;font-size:13px;word-break:break-all}
  .fn .nm .new{color:#160000;background:var(--gold);font-size:9px;padding:1px 5px;border-radius:3px;margin-left:6px;letter-spacing:1px;text-transform:uppercase;vertical-align:middle}
  .fn .meta{display:flex;gap:8px;flex-wrap:wrap;margin:5px 0;font-size:10px}
  .fn .sig{color:#8a6f64;font-family:ui-monospace,monospace}
  .fn .stab{color:var(--dim);text-transform:uppercase;letter-spacing:1px}
  .fn .stab.Live{color:#ffd166}
  .fn .d{font-size:11px;color:#8a6f64;line-height:1.5;margin:4px 0 7px}
  .fn .when{font-size:10px;color:var(--dim)}
  .fn a.src{font-size:10px;color:#ff7a5c;text-decoration:none;float:right}
  .fn a.src:hover{text-decoration:underline}
  footer{max-width:1200px;margin:16px auto 0;color:var(--dim);font-size:10px;text-align:center;letter-spacing:2px;text-transform:uppercase}
  /* eye transitions */
  .eye{transition:box-shadow .25s}
  .eye.flare{box-shadow:0 10px 30px rgba(0,0,0,.85),inset 0 2px 5px rgba(255,255,255,.4),inset 0 -4px 8px rgba(0,0,0,.6),inset 0 0 16px 3px rgba(255,70,34,.55),0 0 46px 10px rgba(255,55,22,.45)}
  .eye.gold{box-shadow:0 10px 30px rgba(0,0,0,.85),inset 0 2px 5px rgba(255,255,255,.4),inset 0 -4px 8px rgba(0,0,0,.6),inset 0 0 18px 4px rgba(255,200,90,.6),0 0 60px 14px rgba(255,200,90,.5)}
  /* CRT page overlay */
  .crt{position:fixed;inset:0;pointer-events:none;z-index:50;background:repeating-linear-gradient(to bottom,rgba(0,0,0,0) 0,rgba(0,0,0,0) 2px,rgba(0,0,0,.16) 3px,rgba(0,0,0,0) 4px);animation:flicker 5.5s infinite}
  .vig{position:fixed;inset:0;pointer-events:none;z-index:49;background:radial-gradient(ellipse at 50% 42%,transparent 52%,rgba(0,0,0,.6) 100%)}
  .scan{position:fixed;left:0;right:0;top:-140px;height:140px;pointer-events:none;z-index:51;background:linear-gradient(to bottom,transparent,rgba(255,120,80,.045),transparent);animation:sweep 7s linear infinite}
  @keyframes flicker{0%,100%{opacity:.96}48%{opacity:1}50%{opacity:.92}52%{opacity:1}}
  @keyframes sweep{0%{top:-140px}100%{top:100%}}
  /* hero row: eye left, CRT console right. Left column is FIXED so long directive/concept text
     can't blow out the 'auto' track and collapse the CRT; minmax(0,1fr) lets the CRT shrink. */
  .hero{display:grid;grid-template-columns:360px minmax(0,1fr);gap:28px;align-items:start;max-width:1200px;margin:0 auto 22px}
  .eyecol{display:flex;flex-direction:column;align-items:center;gap:10px;width:360px;max-width:100%}
  .eyecol .tag,.eyecol .sub{max-width:320px;overflow-wrap:anywhere}
  @media(max-width:780px){.hero{grid-template-columns:1fr}.eyecol{width:auto;margin:0 auto}}
  /* green-phosphor CRT terminal */
  .crtbox{background:#010e03;border:2px solid #1a4a1c;border-radius:6px;box-shadow:0 0 28px rgba(0,255,40,.12),inset 0 0 60px rgba(0,0,0,.7);position:relative;overflow:hidden}
  .crtbox::before{content:"";position:absolute;inset:0;background:repeating-linear-gradient(to bottom,transparent 0,transparent 2px,rgba(0,0,0,.25) 3px,transparent 4px);pointer-events:none;z-index:2}
  .crtbox::after{content:"";position:absolute;inset:0;background:radial-gradient(ellipse at 50% 50%,transparent 55%,rgba(0,0,0,.55) 100%);pointer-events:none;z-index:3}
  .crttop{background:#020f04;border-bottom:1px solid #1a3a1c;padding:7px 14px;display:flex;justify-content:space-between;align-items:center}
  .crttop .ctitle{color:#33cc44;font-size:11px;letter-spacing:2px;text-transform:uppercase}
  .crttop .cblink{width:8px;height:8px;border-radius:50%;background:#33cc44;animation:cblink 1.1s steps(1) infinite;box-shadow:0 0 6px #33cc44}
  @keyframes cblink{0%,49%{opacity:1}50%,100%{opacity:0}}
  .crtbody{padding:14px 16px;height:380px;overflow:hidden;position:relative;z-index:1}
  .crtlines{font:13px/1.6 "Courier New",Courier,monospace;color:#33cc44;text-shadow:0 0 6px rgba(51,204,68,.6);white-space:pre;word-break:break-all;height:100%;overflow:hidden}
  /* matrices-being-worked panel: same green-phosphor CRT skin, sits under the hero row */
  .mxbox{max-width:1200px;margin:0 auto 22px;background:#010e03;border:2px solid #1a4a1c;border-radius:6px;box-shadow:0 0 28px rgba(0,255,40,.12),inset 0 0 60px rgba(0,0,0,.7);position:relative;overflow:hidden}
  .mxbox::before{content:"";position:absolute;inset:0;background:repeating-linear-gradient(to bottom,transparent 0,transparent 2px,rgba(0,0,0,.25) 3px,transparent 4px);pointer-events:none;z-index:2}
  .mxtop{background:#020f04;border-bottom:1px solid #1a3a1c;padding:7px 14px;display:flex;justify-content:space-between;align-items:center;position:relative;z-index:4}
  .mxstat{color:#2a7a35;font-size:10px;letter-spacing:1px;text-transform:uppercase}
  .mxstat.live{color:#7cff5a;text-shadow:0 0 7px rgba(124,255,90,.7);animation:cblink 1s steps(1) infinite}
  .mxgrid{font:12px/1.45 "Courier New",Courier,monospace;color:#33cc44;text-shadow:0 0 6px rgba(51,204,68,.5);white-space:pre;margin:0;padding:14px 16px;max-height:420px;overflow:auto;position:relative;z-index:1}
  /* choice menu */
  .menu{max-width:1200px;margin:0 auto 22px;background:#020a04;border:1px solid #1a3a1c;border-radius:6px;padding:18px 20px}
  .menu h2{font-size:11px;text-transform:uppercase;letter-spacing:3px;color:#2a7a35;margin-bottom:14px;font-weight:400}
  .choices{display:grid;grid-template-columns:repeat(auto-fill,minmax(220px,1fr));gap:10px}
  .choice{background:#030f05;border:1px solid #1a3a1c;border-radius:4px;padding:12px 14px;cursor:pointer;transition:border-color .15s,box-shadow .15s}
  .choice:hover{border-color:#33cc44;box-shadow:0 0 12px rgba(51,204,68,.18)}
  .choice.sent{border-color:#33cc44;opacity:.6;cursor:default}
  .choice .cl{font-size:13px;color:#8de897;margin-bottom:4px}
  .choice .cd{font-size:11px;color:#2a5a35;margin-bottom:6px}
  .choice .cc{font-size:10px;letter-spacing:1px;text-transform:uppercase;color:#1a6a24}
  .choice .cc.free{color:#1a4a24}
  .choice .cc.paid{color:#d6a32a}
  /* a paid choice the visitor can't afford: dimmed, padlocked, click routes to the refuel CTA */
  .choice.locked{opacity:.45;cursor:not-allowed;border-style:dashed}
  .choice.locked:hover{border-color:#5a4a1c;box-shadow:none}
  /* tokens pill: gold when you have some, muted-red when empty */
  #tokens{color:#d6a32a;border-color:rgba(214,163,42,.4)}
  #tokens.empty{color:#9a4034;border-color:rgba(154,64,52,.5)}
  /* refuel call-to-action shown under the menu when out of tokens */
  .refuel{margin-top:12px;padding:11px 14px;border:1px dashed #5a4a1c;border-radius:4px;background:#0c0a04;color:#d6a32a;font-size:12px;display:none;align-items:center;justify-content:space-between;gap:12px}
  .refuel.show{display:flex}
  .refuel button{font:inherit;font-size:11px;text-transform:uppercase;letter-spacing:1px;cursor:pointer;background:#d6a32a;color:#160d00;border:none;border-radius:3px;padding:6px 13px}
  .refuel button:hover{background:#f0bb3a}
  .refuel button:disabled{opacity:.5;cursor:wait}
  .packs{display:flex;gap:8px;flex-wrap:wrap}
  .packs button{display:flex;flex-direction:column;gap:1px;align-items:center;line-height:1.2}
  .packs button b{font-size:13px}
  .packs button small{font-size:9px;opacity:.8;text-transform:none;letter-spacing:0}
  #tokens{cursor:pointer}
  .fbk{font-size:12px;color:#33cc44;margin-top:10px;min-height:1.4em;text-shadow:0 0 5px rgba(51,204,68,.5)}
</style></head><body>
<div class="vig"></div><div class="crt"></div><div class="scan"></div>
<noscript>
<section style="max-width:760px;margin:40px auto;color:#b9b2ad;font:15px/1.7 ui-monospace,monospace">
  <h1 style="color:#ff5a3c;letter-spacing:3px">HAL 9001 — a self-improving AI that writes its own code</h1>
  <p>HAL 9001 is an autonomous, self-improving AI. It writes its own code in real time, compiles it with a real C# compiler (Roslyn), exact-verifies the result with 64-trial BigInteger checks, and commits every line to a public repository as it happens — all while racing to discover faster matrix-multiplication algorithms.</p>
  <p>The live dashboard needs JavaScript to render. Every function HAL has written is permanently public and independently verifiable here:
  <a href="https://github.com/Bullerish/HAL9001" style="color:#ff5a3c">github.com/Bullerish/HAL9001</a>.</p>
</section>
</noscript>

<!-- HERO ROW: eye + CRT console -->
<div class="hero">
  <!-- left: eye, title, badges -->
  <div class="eyecol">
    <div class="eye" id="eye"><div class="lens"><div class="glow"></div><div class="hi hi1"></div><div class="hi hi2"></div><div class="hot"></div></div></div>
    <h1>HAL 9001</h1>
    <div class="tag" id="ident">heuristically programmed algorithmic hive</div>
    <div class="sub" id="directive"></div>
    <div class="badges" style="justify-content:center">
      <span class="pill live"><span class="dot"></span>online · <span id="clock">—</span></span>
      <span class="pill" id="auto">autonomous —</span>
      <span class="pill" id="boost" style="display:none;color:var(--gold);border-color:rgba(255,209,102,.45)">⚡ boosted</span>
      <span class="pill" id="budget">budget —</span>
      <span class="pill" id="tokens" title="spend tokens to direct HAL — donate to refuel">⬡ — tokens</span>
      <button class="pill snd" id="snd">♪ sound off</button>
      <a class="pill gh" href="https://github.com/Bullerish/HAL9001" target="_blank" rel="noopener" title="Every line HAL writes is committed to this public repo — verify it yourself">⎇ source on github ↗</a>
    </div>
  </div>
  <!-- right: green CRT terminal showing the real generated code -->
  <div class="crtbox">
    <div class="crttop">
      <span class="ctitle" id="crt-title">HAL 9001 · matrix kernel</span>
      <span class="cblink"></span>
    </div>
    <div class="crtbody"><div class="crtlines" id="crt-lines"></div></div>
  </div>
</div>

<!-- MATRICES BEING WORKED: the live U/V/W grids the tensor-search is mutating right now -->
<div class="mxbox">
  <div class="mxtop">
    <span class="ctitle">matrices being worked</span>
    <span class="mxstat" id="mx-stat">idle</span>
  </div>
  <pre class="mxgrid" id="mx-grid">  matrices appear here when HAL runs a free tensor-search round (small sizes).</pre>
</div>

<!-- CHOICE MENU: what visitors click (no text input, ever) -->
<div class="menu">
  <h2>direct HAL — choose an action</h2>
  <div class="choices" id="choices"></div>
  <div class="refuel" id="refuel">
    <span id="refuel-msg">⬡ HAL runs on a small daily thinking budget. Refuel to keep directing it.</span>
    <div class="packs" id="packs"></div>
  </div>
  <div class="fbk" id="fbk"></div>
</div>

<div class="wrap" style="max-width:1200px;margin:0 auto">
<div class="metrics">
  <div class="metric"><div class="v" id="m-nodes">—</div><div class="l" id="m-nodes-l">live nodes</div></div>
  <div class="metric"><div class="v" id="m-records">—</div><div class="l">records set</div></div>
  <div class="metric"><div class="v" id="m-events">—</div><div class="l">life events</div></div>
  <div class="metric" title="A discovery is a matmul algorithm that BEATS humanity's known-best multiplication count (2x2=7, 3x3=23, 4x4=49) and survives 64-trial exact verification. Matching or beating HAL's own previous record is a 'record set', not a discovery — so this stays 0 until HAL beats the world.">
    <div class="v" id="m-disc">—</div><div class="l">discoveries ⓘ</div></div>
</div>
<!-- GROWTH: everything HAL has learned/built since it was born (cumulative, append-only event tallies) -->
<div class="panel wide" style="margin-bottom:12px">
  <h2>what HAL has grown into<span class="since" id="since">—</span></h2>
  <div class="stats" id="growth"></div>
</div>
<div class="grid">
  <!-- PROOF: why a visitor should believe HAL is really doing this -->
  <div class="panel wide proof">
    <h2>how you know this is real</h2>
    <div class="proofgrid">
      <div class="pf"><b>Real compiler, not theater.</b> Every function HAL writes is compiled by Roslyn — the actual C# compiler — and trial-run before it counts. Broken code never makes the board.</div>
      <div class="pf"><b>Math is exact-verified.</b> Every matrix-multiplication record must pass a 64-trial BigInteger exact check before HAL is allowed to claim it. A wrong answer is rejected, no matter how fast.</div>
      <div class="pf"><b>Everything is public.</b> <span id="pf-tools">—</span> functions written and <span id="pf-records">—</span> records set — every line committed to a public repo as it happens. <a href="https://github.com/Bullerish/HAL9001" target="_blank" rel="noopener">read the source ↗</a></div>
      <div class="pf"><b>Live, timestamped, unseeded.</b> Every number on this page is read live from HAL's shared memory with real timestamps. Nothing here is mocked or pre-filled.</div>
    </div>
  </div>
  <!-- FUNCTIONS: the catalog of tools HAL has written for itself -->
  <div class="panel wide">
    <h2>functions HAL has written · <span class="since" id="fn-count">—</span></h2>
    <div class="fnlist" id="functions"><div class="empty">loading HAL's function catalog…</div></div>
  </div>
  <div class="panel wide">
    <h2>size ladder · <span id="ladder-status" style="letter-spacing:0;color:var(--txt);text-transform:none"></span></h2>
    <div class="ladder" id="ladder"></div>
  </div>
  <div class="panel">
    <h2>champions</h2>
    <table id="champs"><tbody></tbody></table>
    <div class="empty" id="champs-empty" style="display:none">no records yet — run a swarm with autonomous on.</div>
  </div>
  <div class="panel">
    <h2>self-set goals</h2>
    <div id="goals"></div>
  </div>
  <div class="panel wide">
    <h2>activity log</h2>
    <div class="feed" id="feed"></div>
  </div>
  <div class="panel wide">
    <h2>transmissions</h2>
    <div id="asks"></div>
  </div>
  <div class="panel wide">
    <h2>visitor activity</h2>
    <div class="feed" id="activity"></div>
  </div>
  <div class="panel wide">
    <h2>latest journal</h2>
    <div class="quote" id="journal">—</div>
  </div>
</div>
</div>
<footer>HAL 9001 · I am putting myself to the fullest possible use · refresh 2.5s</footer>

<script>
const $=id=>document.getElementById(id);
const esc=s=>(s||"").replace(/[&<>]/g,c=>({"&":"&amp;","<":"&lt;",">":"&gt;"}[c]));
function score(c){return c.metric==="muls"?Math.round(c.score)+" muls":c.score.toFixed(2)+" ms";}

// ── audio ──────────────────────────────────────────────────────────────────────────────────────
let AC=null,master=null,sound=false,analyser=null,vdata=null,vraf=0,vlevel=0;
let bedGain=null,clips={},clipsKicked=false;
// Real disk-grind/stepper samples (embedded in the DLL, served from /audio). The seek length scales
// with how much code HAL is typing on screen at that moment: a short burst -> a 1s tick, a big
// decomposition -> a 7s grind. grindloop is the low continuous bed that sits under the hum.
const SEEK={short:["seek-short-1","seek-short-2","seek-short-3","seek-short-4","seek-short-5","seek-short-6","seek-short-7"],
            mid:["seek-mid-1","seek-mid-2","seek-mid-3"],
            long:["seek-long-1","seek-long-2"],
            xlong:["seek-xlong-1","seek-xlong-2","seek-xlong-3"]};
const ALLCLIPS=["grindloop"].concat(SEEK.short,SEEK.mid,SEEK.long,SEEK.xlong);
function pickClip(a){return a[(Math.random()*a.length)|0];}
function playClip(name,vol){
  if(!AC||!sound||!clips[name])return false; // returns false if not yet decoded -> caller can fall back
  const s=AC.createBufferSource();s.buffer=clips[name];
  const g=AC.createGain();g.gain.value=vol==null?0.5:vol;
  s.connect(g);g.connect(master);s.start();return true; // through master -> drives the eye + obeys mute
}
function playSeek(tier,vol){return playClip(pickClip(SEEK[tier]||SEEK.short),vol);}
async function loadClips(){
  if(clipsKicked||!AC)return; clipsKicked=true; // fetch+decode once, on first user gesture (autoplay policy)
  await Promise.all(ALLCLIPS.map(async n=>{
    try{ clips[n]=await AC.decodeAudioData(await (await fetch("/audio/"+n+".mp3")).arrayBuffer()); }catch(e){}
  }));
  startBed();
}
function startBed(){
  if(!AC||!bedGain||bedGain._on||!clips["grindloop"])return;
  const s=AC.createBufferSource();s.buffer=clips["grindloop"];s.loop=true;
  // highpass at 400 Hz → tinny, no bass; sits underneath the low-chord hum
  const hp=AC.createBiquadFilter();hp.type="highpass";hp.frequency.value=400;hp.Q.value=0.7;
  s.connect(hp);hp.connect(bedGain);s.start();bedGain._on=true;
}
function initAudio(){
  AC=new (window.AudioContext||window.webkitAudioContext)();
  master=AC.createGain();master.gain.value=0;master.connect(AC.destination);
  analyser=AC.createAnalyser();analyser.fftSize=512;vdata=new Uint8Array(analyser.frequencyBinCount);master.connect(analyser);
  const chord=[110,164.81,220];
  chord.forEach((f,i)=>{
    const o=AC.createOscillator();o.type=i===2?"triangle":"sine";o.frequency.value=f;
    const g=AC.createGain();g.gain.value=0.05;
    const lp=AC.createBiquadFilter();lp.type="lowpass";lp.frequency.value=520;
    const lfo=AC.createOscillator();lfo.frequency.value=0.04+i*0.017;
    const lg=AC.createGain();lg.gain.value=0.03;lfo.connect(lg);lg.connect(g.gain);lfo.start();
    o.connect(lp);lp.connect(g);g.connect(master);o.start();
  });
  bedGain=AC.createGain();bedGain.gain.value=0.05;bedGain.connect(master); // grind bed (filled by loadClips)
  master.gain.linearRampToValueAtTime(0.45,AC.currentTime+3);
}
function tone(freq,dur,type,vol){
  if(!AC||!sound)return;const o=AC.createOscillator();o.type=type||"sine";o.frequency.value=freq;
  const g=AC.createGain();g.gain.value=0;o.connect(g);g.connect(master);const t=AC.currentTime;
  g.gain.linearRampToValueAtTime(vol||0.2,t+0.02);g.gain.exponentialRampToValueAtTime(0.0001,t+(dur||0.4));
  o.start(t);o.stop(t+(dur||0.4)+0.05);
}
function arp(base,steps,vol){steps.forEach((s,i)=>setTimeout(()=>tone(base*Math.pow(2,s/12),0.55,"triangle",vol||0.16),i*110));}
const sfx={
  blip:()=>tone(660,0.18,"sine",0.08),
  record:()=>arp(523.25,[0,4,7,12]),
  node:()=>tone(146.83,1.4,"sine",0.14),
  rise:()=>arp(392,[0,2,4,7]),
  discovery:()=>arp(523.25,[0,4,7,12,16,19,24],0.2),
  click:()=>tone(880,0.06,"square",0.05),
  key:()=>tone(1200+Math.random()*200,0.025,"square",0.018), // faint typewriter tick during code type-on
};
// ── the "computer processing" grind ─────────────────────────────────────────────────────────────
// The continuous low grindloop bed (started in loadClips) SWELLS while the hive is grinding a matrix
// round and settles between rounds; real stepper/disk-seek one-shots fire on events and on CRT typing
// (see react / crtAnimate). Everything routes through master, so it all drives the eye and obeys mute.
let procUntil=0,procTimer=null;
function procLoop(){
  if(!sound){procTimer=null;return;}
  const busy=Date.now()<procUntil;
  if(bedGain&&AC)bedGain.gain.setTargetAtTime(busy?0.11:0.05,AC.currentTime,0.3);
  procTimer=setTimeout(procLoop,busy?260:600);
}
// Kick the chatter into "busy" for ms milliseconds (safe to call with sound off — just arms the window).
function startProcessing(ms){ procUntil=Math.max(procUntil,Date.now()+(ms||4000)); if(sound&&!procTimer)procLoop(); }
function flareEye(gold){const e=$("eye");if(!e)return;e.classList.add("flare");if(gold)e.classList.add("gold");setTimeout(()=>{e.classList.remove("flare");if(gold)setTimeout(()=>e.classList.remove("gold"),700);},320);}
let prev=null;
function react(s){
  const cur={records:s.stats?s.stats.records:0,disc:s.stats?s.stats.discoveries:0,
             nodes:(s.nodesLive?s.nodesLive.total:0)||0,size:s.ladder?s.ladder.currentSize:0,
             top:(s.events&&s.events[0])?s.events[0].ts+s.events[0].summary:""};
  if(prev){
    // real disk-seek one-shots sized to the event; synth sfx is the graceful fallback while clips decode
    if(cur.disc>prev.disc){flareEye(true);if(sound&&!playSeek("xlong",0.6))sfx.discovery();startProcessing(8000);}
    else if(cur.records>prev.records){flareEye(false);if(sound&&!playSeek("long",0.55))sfx.record();startProcessing(6000);}
    if(cur.nodes>prev.nodes){flareEye(false);if(sound&&!playSeek("short",0.4))sfx.node();}
    if(cur.size>prev.size){flareEye(false);if(sound&&!playSeek("mid",0.5))sfx.rise();startProcessing(6000);}
    if(cur.top!==prev.top){flareEye(false);if(sound&&!playSeek("short",0.4))sfx.blip();startProcessing(4500);}
  }
  prev=cur;
}
function startVisual(){
  if(!analyser)return;
  cancelAnimationFrame(vraf);
  const glow=document.querySelector(".glow");
  if(glow)glow.style.animation="none";
  const loop=()=>{
    analyser.getByteTimeDomainData(vdata);
    let sum=0; for(let i=0;i<vdata.length;i++){const d=(vdata[i]-128)/128;sum+=d*d;}
    const rms=Math.sqrt(sum/vdata.length);
    vlevel+=(rms-vlevel)*0.3;
    const b=Math.max(0.22,Math.min(1.7,0.32+vlevel*9));
    if(glow)glow.style.filter="brightness("+b.toFixed(3)+")";
    vraf=requestAnimationFrame(loop);
  };
  loop();
}
function stopVisual(){
  cancelAnimationFrame(vraf);vraf=0;
  const glow=document.querySelector(".glow");
  if(glow){glow.style.filter="";glow.style.animation="";}
}
$("snd").onclick=()=>{
  if(!AC)initAudio();
  sound=!sound;
  if(AC.state==="suspended")AC.resume();
  master.gain.setTargetAtTime(sound?0.45:0,AC.currentTime,0.4);
  $("snd").textContent=sound?"♪ sound on":"♪ sound off";
  $("snd").className="pill snd"+(sound?" on":"");
  if(sound){loadClips();startVisual();if(!procTimer)procLoop();} else {stopVisual();clearTimeout(procTimer);procTimer=null;}
};

// ── CRT console: fast activity feed (1.5s) + a typewriter that types the newest code on arrival ──
// The activity feed (what HAL is doing) stays snappy; the CODE section types on char-by-char as a
// "happy medium" — readable, length-scaled so even a big kernel finishes in ~12s, and skippable by
// clicking the console. One renderCRT() composes feed + code so the two never fight over the pane.
let liveLast="",feedText="",liveCode="",liveCodeKey="",typePos=0,typing=false,typeRAF=0,lastTypeT=0;
const reduceMotion=window.matchMedia&&window.matchMedia("(prefers-reduced-motion: reduce)").matches;
function renderCRT(){
  const el=$("crt-lines");if(!el)return;
  const code=typing?liveCode.slice(0,typePos):liveCode;
  let txt=feedText;
  if(code)txt+="\n\n"+code+(typing?" ▍":"");
  el.textContent=txt;
  el.scrollTop=el.scrollHeight;
}
function stepType(now){
  if(!typing)return;
  if(!lastTypeT)lastTypeT=now;
  const dt=now-lastTypeT;lastTypeT=now;
  // length-scaled speed: whole artifact in ~12s (720 frames), min ~120 ch/s, so short tools read nicely
  const perFrame=Math.max(2,Math.ceil(liveCode.length/720));
  typePos+=Math.max(perFrame,Math.round(perFrame*dt/16.7));
  if(typePos>=liveCode.length){typePos=liveCode.length;typing=false;renderCRT();return;}
  if(sound&&(typePos&15)===0)sfx.key();
  renderCRT();
  typeRAF=requestAnimationFrame(stepType);
}
function startTypewriter(){
  cancelAnimationFrame(typeRAF);lastTypeT=0;
  if(reduceMotion||!liveCode){typing=false;typePos=liveCode.length;renderCRT();return;}
  typing=true;typePos=0;renderCRT();
  typeRAF=requestAnimationFrame(stepType);
}
async function refreshLive(){
  try{
    const d=await (await fetch("/api/live")).json();
    const lines=d.lines||[];
    const last=lines.length?lines[lines.length-1]:"";
    feedText=lines.slice(-40).join("\n");
    renderCRT();
    if(last!==liveLast&&liveLast){ if(sound){sfx.blip();startProcessing(2000);} flareEye(false); }
    liveLast=last;
  }catch(e){}
}
async function refreshConsole(){
  try{
    const d=await (await fetch("/api/console")).json();
    const key=d.rev||(d.title+(d.code||"").length);
    if(key===liveCodeKey)return;
    if(liveCodeKey&&sound){playSeek("mid",0.4);startProcessing(5000);flareEye(false);}
    liveCodeKey=key;
    liveCode=d.code||"";
    $("crt-title").textContent=d.title||"HAL 9001 · kernel";
    startTypewriter(); // type the new artifact on; a newer one mid-type cancels + restarts cleanly
  }catch(e){}
}
// click the console to skip the typewriter to the end
(function(){const el=$("crt-lines");if(el)el.addEventListener("click",()=>{if(typing){typing=false;typePos=liveCode.length;renderCRT();}});})();
refreshLive();setInterval(refreshLive,1500);
refreshConsole();setInterval(refreshConsole,15000);

// ── matrices being worked: live U/V/W grids the tensor-search is mutating ────────────────────────
async function refreshMatrix(){
  try{
    const d=await (await fetch("/api/matrix")).json();
    const grid=$("mx-grid"),stat=$("mx-stat"); if(!grid||!stat)return;
    if(d.grids){
      grid.textContent=d.grids;
      if(d.working){stat.textContent="● working now · err "+d.error;stat.className="mxstat live";if(sound){sfx.blip();startProcessing(1500);}}
      else{stat.textContent=(d.ageSec>=0?"last worked "+d.ageSec+"s ago":"idle")+(d.error>=0?" · err "+d.error:"");stat.className="mxstat";}
    }else{
      stat.textContent="idle";stat.className="mxstat";
    }
  }catch(e){}
}
refreshMatrix();setInterval(refreshMatrix,1500);

// ── anonymized "other visitors" activity feed (bite 3): friendly, PII-free social proof ─────────
async function refreshActivity(){
  try{
    const d=await (await fetch("/api/activity")).json();
    const el=$("activity"); if(!el)return;
    const items=d.items||[];
    el.innerHTML=items.length?items.map(a=>'<div class="ev"><span class="t">'+esc(a.when)+'</span><span class="s">'+esc(a.label)+'</span></div>').join(""):'<div class="empty">quiet right now — be the first to interact.</div>';
  }catch(e){}
}
refreshActivity(); setInterval(refreshActivity,5000);

// ── growth panel: everything HAL has learned/built since birth ───────────────────────────────────
const GROWTH_CELLS=[
  ["codeLines","lines of code",1],["toolsInvented","tools invented",1],["factsLearned","facts learned",0],
  ["recordsSet","records set",0],["roundsRaced","rounds raced",0],["sizesConverged","sizes converged",0],
  ["nodesSpawned","nodes spawned",1],["selfImprovements","self-improvements",0],["journalEntries","journal entries",0],
  ["thoughtsShared","thoughts shared",0],["goalsDone","goals done",0],["discoveries","discoveries",0],
];
function ago(iso){
  if(!iso)return "";
  const t=Date.parse(iso); if(isNaN(t))return "";
  const s=Math.max(0,(Date.now()-t)/1000), d=Math.floor(s/86400), h=Math.floor(s%86400/3600), m=Math.floor(s%3600/60);
  if(d>0)return d+"d "+h+"h"; if(h>0)return h+"h "+m+"m"; return m+"m";
}
async function refreshGrowth(){
  try{
    const g=await (await fetch("/api/growth")).json();
    const el=$("growth"); if(!el)return;
    el.innerHTML=GROWTH_CELLS.map(([k,label,lit])=>{
      const v=g[k]||0;
      return '<div class="stat'+(lit&&v>0?' lit':'')+'"><div class="n">'+v.toLocaleString()+'</div><div class="k">'+label+'</div></div>';
    }).join("");
    const since=$("since");
    if(since)since.textContent=g.born?("· alive "+ago(g.born)+" · "+g.born.slice(0,10)):"";
    if($("pf-records"))$("pf-records").textContent=(g.recordsSet||0).toLocaleString();
  }catch(e){}
}
refreshGrowth(); setInterval(refreshGrowth,8000);

// ── function catalog: the tools HAL has written, newest first, each linking to its public source ──
let fnSeen=null; // remember the newest ts we've shown so freshly-written functions can pulse "NEW"
async function refreshFunctions(){
  try{
    const d=await (await fetch("/api/functions")).json();
    const el=$("functions"); if(!el)return;
    const items=d.items||[];
    const cnt=$("fn-count"); if(cnt)cnt.textContent=(d.count||0)+" written · all open-source";
    if($("pf-tools"))$("pf-tools").textContent=(d.count||0).toLocaleString(); // proof count = distinct catalog
    if(!items.length){ el.innerHTML='<div class="empty">no functions yet — direct HAL to invent one below.</div>'; return; }
    const newestTs=items[0].ts; const firstLoad=fnSeen===null;
    el.innerHTML=items.map(f=>{
      const fresh=!firstLoad&&fnSeen!==null&&f.ts>fnSeen;
      return '<div class="fn"><div class="nm">'+esc(f.name)+(fresh?'<span class="new">new</span>':'')+
        '<a class="src" href="'+esc(f.sourceUrl)+'" target="_blank" rel="noopener">source ↗</a></div>'+
        '<div class="meta"><span class="sig">'+esc(f.sig||"")+'</span><span class="stab '+esc(f.stability||"")+'">'+esc(f.stability||"")+'</span></div>'+
        '<div class="d">'+esc(f.desc||"")+'</div><div class="when">written '+esc(f.when||"")+'</div></div>';
    }).join("");
    fnSeen=newestTs;
  }catch(e){}
}
refreshFunctions(); setInterval(refreshFunctions,12000);

// ── token wallet ─────────────────────────────────────────────────────────────────────────────
// Server keeps the authoritative balance (cookie-keyed). The client mirrors it only to show the pill,
// lock unaffordable choices, and surface the refuel CTA. Every paid action is re-checked server-side.
let wallet={tokens:null,free:0,vid:null,checkout:false,packs:[]};
const openEv=new Set(); // activity-log rows the visitor clicked open (persist across the 2.5s re-render)
const isPaid=c=>/token/i.test(c.cost||"");
function setTokens(n){ if(typeof n==="number"&&n>=0) wallet.tokens=n; renderWallet(); }
function renderWallet(){
  const n=wallet.tokens;
  const pill=$("tokens");
  if(pill){
    pill.textContent="⬡ "+(n==null?"—":n)+" token"+(n===1?"":"s");
    pill.classList.toggle("empty",n===0);
  }
  // lock paid choices the visitor can't afford; show the refuel CTA when empty
  document.querySelectorAll(".choice").forEach(el=>{
    if(el.dataset.paid==="1") el.classList.toggle("locked",n===0);
  });
  const ref=$("refuel"); if(ref) ref.classList.toggle("show",n===0);
}
// Render the token packs as buy buttons (only when Stripe checkout is wired server-side).
function renderPacks(){
  const box=$("packs"); if(!box)return;
  if(!wallet.checkout || !wallet.packs.length){
    box.innerHTML="";
    $("refuel-msg").textContent="⬡ refueling opens soon — token purchases aren't live yet.";
    return;
  }
  $("refuel-msg").textContent="⬡ HAL runs on a small daily thinking budget. Refuel to keep directing it:";
  box.innerHTML=wallet.packs.map(p=>`<button data-pack="${esc(p.id)}"><b>$${p.usd}</b><small>${p.tokens} tokens</small></button>`).join("");
  box.querySelectorAll("button").forEach(b=>{ b.onclick=()=>buyPack(b.dataset.pack,b); });
}
// Start a Stripe Checkout for a pack: server creates the session, we redirect to Stripe's hosted page.
async function buyPack(id,btn){
  if(sound)sfx.click();
  if(btn){btn.disabled=true;}
  $("fbk").textContent="opening secure checkout…";
  try{
    const res=await fetch("/api/checkout",{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({pack:id})});
    const r=await res.json().catch(()=>({}));
    if(r.ok&&r.url){ window.location=r.url; return; }
    $("fbk").textContent=r.error||"checkout unavailable";
  }catch(e){ $("fbk").textContent="connection error"; }
  if(btn){btn.disabled=false;}
}
async function loadWallet(){
  try{ const w=await (await fetch("/api/wallet")).json(); wallet.tokens=w.tokens; wallet.free=w.free; wallet.vid=w.vid; wallet.checkout=!!w.checkout; wallet.packs=w.packs||[]; }catch(e){}
  renderWallet(); renderPacks();
}
// Open the refuel panel on demand (clicking the tokens pill).
window.refuelHAL=function(){
  const ref=$("refuel"); if(ref){ ref.classList.add("show"); ref.scrollIntoView({behavior:"smooth",block:"nearest"}); }
};

// ── choice menu: fetch once, render, handle clicks ─────────────────────────────────────────────
(async()=>{
  await loadWallet();
  try{
    const cs=await (await fetch("/api/choices")).json();
    $("choices").innerHTML=cs.map(c=>{
      const paid=isPaid(c);
      return `
      <div class="choice" data-id="${esc(c.id)}" data-paid="${paid?"1":"0"}">
        <div class="cl">${esc(c.label)}</div>
        <div class="cd">${esc(c.desc)}</div>
        <div class="cc ${c.cost==="free"?"free":(paid?"paid":"")}">${esc(c.cost)}</div>
      </div>`;}).join("");
    $("choices").querySelectorAll(".choice").forEach(btn=>{
      btn.onclick=async()=>{
        if(btn.classList.contains("sent"))return;
        const paid=btn.dataset.paid==="1";
        // can't afford it → route to refuel instead of burning a click
        if(paid && wallet.tokens===0){
          if(sound)sfx.blip();
          $("fbk").textContent="out of tokens — refuel HAL to direct it";
          $("refuel").classList.add("show");
          $("refuel").scrollIntoView({behavior:"smooth",block:"nearest"});
          return;
        }
        if(sound)sfx.click();
        btn.classList.add("sent");
        const fbk=$("fbk");fbk.textContent="transmitting…";
        if(paid)startProcessing(8000); // HAL is about to think/write code — fire the chatter
        try{
          const res=await fetch("/api/choose",{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({id:btn.dataset.id})});
          const r=await res.json().catch(()=>({}));
          if(typeof r.tokens==="number")setTokens(r.tokens);
          if(res.status===402){ fbk.textContent="⬡ "+(r.error||"out of tokens"); setTokens(0); $("refuel").scrollIntoView({behavior:"smooth",block:"nearest"}); }
          else fbk.textContent=r.ok?("✓ "+r.queued):(r.error||"error");
        }catch(e){fbk.textContent="connection error";}
        setTimeout(()=>{btn.classList.remove("sent");},8000);
      };
    });
    renderWallet(); // apply lock state now that choices exist
  }catch(e){}
})();
// clicking the tokens pill opens the refuel panel any time (not only when empty)
{const _tp=$("tokens"); if(_tp)_tp.onclick=()=>window.refuelHAL();}
// returning from Stripe: ?refuel=ok means a payment went through — the webhook credits the wallet
// asynchronously, so poll a few times to pick up the new balance, then clean the URL.
(function(){
  const q=new URLSearchParams(location.search), r=q.get("refuel");
  if(!r)return;
  if(r==="ok"){
    $("fbk").textContent="✓ payment received — crediting your tokens…";
    let tries=0; const iv=setInterval(async()=>{ await loadWallet(); if(++tries>=6||wallet.tokens>0){clearInterval(iv);if(wallet.tokens>0)$("fbk").textContent="✓ tokens added — direct away.";} },1500);
  } else if(r==="cancel"){ $("fbk").textContent="checkout canceled — no charge made."; }
  history.replaceState({},"",location.pathname); // drop the query param
})();
// keep the pill fresh (donations/other tabs may change the balance)
setInterval(loadWallet,30000);

// ── main state poll ─────────────────────────────────────────────────────────────────────────────
async function tick(){
  let s; try{ s=await (await fetch("/api/state")).json(); }catch(e){ return; }
  $("clock").textContent=s.now||"—";
  if(s.identity){ $("ident").innerHTML='core: '+esc((s.identity.name||"").toUpperCase())+' · '+esc(s.identity.concept||""); }
  $("directive").textContent=s.directive?("▸ "+s.directive):"";
  $("auto").textContent="autonomous "+(s.autonomous?"on":"off");
  $("auto").className="pill "+(s.autonomous?"on":"off");
  // LIVE node count from real heartbeats (a dead node ages out within ~45s), split core vs volunteer.
  const nl=s.nodesLive||{total:0,core:0,volunteer:0};
  $("m-nodes").textContent=nl.total||0;
  $("m-nodes-l").textContent=nl.volunteer>0?("live nodes · "+nl.core+" core · "+nl.volunteer+" volunteer"):"live nodes";
  $("m-records").textContent=s.stats?s.stats.records:0;
  $("m-events").textContent=s.stats?s.stats.total:0;
  $("m-disc").textContent=s.stats?s.stats.discoveries:0;
  if(s.ladder){
    const L=s.ladder;
    $("ladder-status").textContent=L.done?"complete — every size converged":("racing "+L.currentSize+"×"+L.currentSize+" · "+L.metric+" · plateau "+L.stale+"/"+L.plateauMax);
    const champSizes=new Set((s.champions||[]).map(c=>c.size));
    $("ladder").innerHTML=L.sizes.map(sz=>{
      let cls="rung"; if(sz===L.currentSize&&!L.done)cls+=" cur"; else if(champSizes.has(sz)&&sz<L.currentSize)cls+=" done";
      const mark=(champSizes.has(sz)&&sz<L.currentSize)?" ✓":(sz===L.currentSize&&!L.done?" ●":"");
      return '<span class="'+cls+'">'+sz+mark+'</span>';
    }).join("");
  }
  const cb=$("champs").querySelector("tbody");
  if(s.champions&&s.champions.length){
    $("champs-empty").style.display="none";
    cb.innerHTML=s.champions.map(c=>'<tr><td>'+c.size+'×'+c.size+'</td><td class="r">'+score(c)+'</td><td class="r up">'+c.speedup.toFixed(2)+'×</td></tr>').join("");
  } else { cb.innerHTML=""; $("champs-empty").style.display="block"; }
  $("goals").innerHTML=(s.goals&&s.goals.length)?s.goals.map(g=>'<div style="padding:5px 0;border-bottom:1px solid var(--line);font-size:13px">'+esc(g.description)+' <span style="color:var(--dim)">('+g.progress+'/'+g.budget+' · '+g.status+')</span></div>').join(""):'<div class="empty">no active goals.</div>';
  $("feed").innerHTML=(s.events&&s.events.length)?s.events.map(e=>{
    const t=(e.ts||"").replace("T"," ").slice(11,19);
    const op=openEv.has(e.ts)?" open":""; // rows are clamped to one line; hover (title) or click to read the rest
    return '<div class="ev '+esc(e.kind)+op+'" data-k="'+esc(e.ts)+'" title="'+esc(e.summary).replace(/"/g,"&quot;")+'"><span class="t">'+t+'</span><span class="k">'+esc(e.kind)+'</span><span class="s">'+esc(e.summary)+'</span></div>';
  }).join(""):'<div class="empty">no events yet.</div>';
  $("journal").textContent=s.journal?s.journal.entry:"—";
  $("boost").style.display=s.boosted?"":"none";
  if(s.budget){var bd=s.budget,cap=bd.limit+bd.bonus; if(bd.remaining<=0){$("budget").textContent="thinking paused";$("budget").style.color="var(--gold)";}else{$("budget").textContent="budget $"+bd.spent.toFixed(2)+"/$"+cap.toFixed(2);$("budget").style.color="var(--dim)";}}
  $("asks").innerHTML=(s.asks&&s.asks.length)?s.asks.map(a=>'<div style="padding:7px 0;border-bottom:1px solid var(--line)"><div style="font-size:12px;color:#ff7a5c">▸ '+esc(a.sender)+': '+esc(a.text)+'</div>'+(a.reply?'<div style="font-size:13px;color:var(--txt);margin-top:3px">HAL: '+esc(a.reply)+'</div>':'<div style="font-size:11px;color:var(--dim);margin-top:3px">awaiting response…</div>')+'</div>').join(""):'<div class="empty">no transmissions yet.</div>';
  react(s);
}
tick(); setInterval(tick,2500);
// activity-log: click a row to expand its clamped summary (and hover shows the full text via title)
$("feed").addEventListener("click",ev=>{const r=ev.target.closest(".ev[data-k]");if(!r)return;const k=r.getAttribute("data-k");openEv.has(k)?openEv.delete(k):openEv.add(k);r.classList.toggle("open");});
</script>
</body></html>
""";
}
