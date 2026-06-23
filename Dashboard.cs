using System.Diagnostics;
using System.Net;
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
                events.Add(new { ts = e.Timestamp, actor = e.Actor, kind = e.Kind, summary = e.Summary });
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

        bool boosted = false; string? boostUntil = null;
        var asks = new List<object>();
        try { var bu = await core.GetBoostUntilAsync(); if (bu is not null) { boostUntil = bu.Value.ToString("o"); boosted = bu > DateTime.UtcNow; } } catch { }
        try { foreach (var a in await core.RecentAsksAsync(6)) asks.Add(new { sender = a.Sender, text = a.Text, reply = a.Reply, status = a.Status }); } catch { }

        var state = new
        {
            identity,
            directive,
            autonomous,
            boosted,
            boostUntil,
            ladder,
            champions,
            goals,
            journal,
            asks,
            events,
            nodes,
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
        await core.SetMatmulChampionAsync(who, p.Size, "volunteer-contributed", MatmulRace.Metric.Muls, muls, speedup, src);
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
    private sealed record DonatePayload(string? Action, int? Minutes, string? Text, string? From);

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
        ctx.Response.StatusCode = 400; return Err("unknown action");
    }

    private static string Err(string m) => JsonSerializer.Serialize(new { ok = false, error = m }, JsonOpts);

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

    // The whole page — self-contained (no external dependencies, works offline). Polls /api/state.
    private const string Html = """
<!DOCTYPE html>
<html lang="en"><head>
<meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>HAL 9001</title>
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
  .ev{display:flex;gap:8px;padding:5px 0;border-bottom:1px solid var(--line);font-size:12px}
  .ev .t{color:var(--dim);white-space:nowrap}
  .ev .k{color:#a3453a;white-space:nowrap}
  .ev.discovery .k,.ev.discovery .s{color:var(--gold)}
  .ev.contribution-accepted .k,.ev.contribution-accepted .s{color:#ff7a5c}
  .quote{font-style:italic;color:#8a6f64;border-left:2px solid var(--line2);padding-left:12px;line-height:1.7}
  .empty{color:var(--dim);font-size:12px}
  .wide{grid-column:1/-1}
  footer{max-width:1100px;margin:16px auto 0;color:var(--dim);font-size:10px;text-align:center;letter-spacing:2px;text-transform:uppercase}
  .eye{transition:box-shadow .25s}
  .eye.flare{box-shadow:0 10px 30px rgba(0,0,0,.85),inset 0 2px 5px rgba(255,255,255,.4),inset 0 -4px 8px rgba(0,0,0,.6),inset 0 0 16px 3px rgba(255,70,34,.55),0 0 46px 10px rgba(255,55,22,.45)}
  .eye.gold{box-shadow:0 10px 30px rgba(0,0,0,.85),inset 0 2px 5px rgba(255,255,255,.4),inset 0 -4px 8px rgba(0,0,0,.6),inset 0 0 18px 4px rgba(255,200,90,.6),0 0 60px 14px rgba(255,200,90,.5)}
  .crt{position:fixed;inset:0;pointer-events:none;z-index:50;background:repeating-linear-gradient(to bottom,rgba(0,0,0,0) 0,rgba(0,0,0,0) 2px,rgba(0,0,0,.16) 3px,rgba(0,0,0,0) 4px);animation:flicker 5.5s infinite}
  .vig{position:fixed;inset:0;pointer-events:none;z-index:49;background:radial-gradient(ellipse at 50% 42%,transparent 52%,rgba(0,0,0,.6) 100%)}
  .scan{position:fixed;left:0;right:0;top:-140px;height:140px;pointer-events:none;z-index:51;background:linear-gradient(to bottom,transparent,rgba(255,120,80,.045),transparent);animation:sweep 7s linear infinite}
  @keyframes flicker{0%,100%{opacity:.96}48%{opacity:1}50%{opacity:.92}52%{opacity:1}}
  @keyframes sweep{0%{top:-140px}100%{top:100%}}
</style></head><body>
<div class="vig"></div><div class="crt"></div><div class="scan"></div>
<div class="eyewrap">
  <div class="eye" id="eye"><div class="lens"><div class="glow"></div><div class="hi hi1"></div><div class="hi hi2"></div><div class="hot"></div></div></div>
  <h1>HAL 9001</h1>
  <div class="tag" id="ident">heuristically programmed algorithmic hive</div>
  <div class="sub" id="directive"></div>
  <div class="badges">
    <span class="pill live"><span class="dot"></span>online · <span id="clock">—</span></span>
    <span class="pill" id="auto">autonomous —</span>
    <span class="pill" id="boost" style="display:none;color:var(--gold);border-color:rgba(255,209,102,.45)">⚡ boosted</span>
    <button class="pill snd" id="snd">♪ sound off</button>
  </div>
</div>

<div class="wrap">
<div class="metrics">
  <div class="metric"><div class="v" id="m-nodes">—</div><div class="l">active nodes</div></div>
  <div class="metric"><div class="v" id="m-records">—</div><div class="l">records set</div></div>
  <div class="metric"><div class="v" id="m-events">—</div><div class="l">life events</div></div>
  <div class="metric"><div class="v" id="m-disc">—</div><div class="l">discoveries</div></div>
</div>

<div class="grid">
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

let AC=null,master=null,sound=false,analyser=null,vdata=null,vraf=0,vlevel=0;
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
  blip:()=>tone(660,0.18,"sine",0.08),         // a life event ticked by
  record:()=>arp(523.25,[0,4,7,12]),           // new champion — C major run up
  node:()=>tone(146.83,1.4,"sine",0.14),       // a node joined — warm low swell
  rise:()=>arp(392,[0,2,4,7]),                 // climbed a ladder rung
  discovery:()=>arp(523.25,[0,4,7,12,16,19,24],0.2), // a genuine discovery — fanfare
};
function flareEye(gold){const e=$("eye");if(!e)return;e.classList.add("flare");if(gold)e.classList.add("gold");setTimeout(()=>{e.classList.remove("flare");if(gold)setTimeout(()=>e.classList.remove("gold"),700);},320);}
let prev=null;
function react(s){
  const cur={records:s.stats?s.stats.records:0,disc:s.stats?s.stats.discoveries:0,
             nodes:(s.nodes&&s.nodes.length)||0,size:s.ladder?s.ladder.currentSize:0,
             top:(s.events&&s.events[0])?s.events[0].ts+s.events[0].summary:""};
  if(prev){
    if(cur.disc>prev.disc){flareEye(true);if(sound)sfx.discovery();}
    else if(cur.records>prev.records){flareEye(false);if(sound)sfx.record();}
    if(cur.nodes>prev.nodes){flareEye(false);if(sound)sfx.node();}
    if(cur.size>prev.size){flareEye(false);if(sound)sfx.rise();}
    if(cur.top!==prev.top){flareEye(false);if(sound)sfx.blip();}
  }
  prev=cur;
}
// Drive the inner lens brightness from the LIVE audio amplitude: darker at rest, pulsing up with
// the drone's slow swell and spiking on each chime/swell/fanfare — so the eye beats to the sound.
function startVisual(){
  if(!analyser)return;
  cancelAnimationFrame(vraf);
  const glow=document.querySelector(".glow");
  if(glow)glow.style.animation="none"; // let the analyser drive brightness instead of the breathe loop
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
  if(glow){glow.style.filter="";glow.style.animation="";} // restore the gentle CSS breathe
}
$("snd").onclick=()=>{
  if(!AC)initAudio();
  sound=!sound;
  if(AC.state==="suspended")AC.resume();
  master.gain.setTargetAtTime(sound?0.45:0,AC.currentTime,0.4);
  $("snd").textContent=sound?"♪ sound on":"♪ sound off";
  $("snd").className="pill snd"+(sound?" on":"");
  if(sound)startVisual(); else stopVisual();
};
async function tick(){
  let s; try{ s=await (await fetch("/api/state")).json(); }catch(e){ return; }
  $("clock").textContent=s.now||"—";
  if(s.identity){ $("ident").innerHTML='core: '+esc((s.identity.name||"").toUpperCase())+' · '+esc(s.identity.concept||""); }
  $("directive").textContent=s.directive?("▸ "+s.directive):"";
  $("auto").textContent="autonomous "+(s.autonomous?"on":"off");
  $("auto").className="pill "+(s.autonomous?"on":"off");
  $("m-nodes").textContent=(s.nodes&&s.nodes.length)||0;
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
    return '<div class="ev '+esc(e.kind)+'"><span class="t">'+t+'</span><span class="k">'+esc(e.kind)+'</span><span class="s">'+esc(e.summary)+'</span></div>';
  }).join(""):'<div class="empty">no events yet.</div>';

  $("journal").textContent=s.journal?s.journal.entry:"—";

  $("boost").style.display=s.boosted?"":"none";
  $("asks").innerHTML=(s.asks&&s.asks.length)?s.asks.map(a=>'<div style="padding:7px 0;border-bottom:1px solid var(--line)"><div style="font-size:12px;color:#ff7a5c">▸ '+esc(a.sender)+': '+esc(a.text)+'</div>'+(a.reply?'<div style="font-size:13px;color:var(--txt);margin-top:3px">HAL: '+esc(a.reply)+'</div>':'<div style="font-size:11px;color:var(--dim);margin-top:3px">awaiting response…</div>')+'</div>').join(""):'<div class="empty">no transmissions yet.</div>';
  react(s);
}
tick(); setInterval(tick,2500);
</script>
</body></html>
""";
}
