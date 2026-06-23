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
        try
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "/";
            if (path == "/api/state")
                Write(ctx, "application/json", await GatherStateAsync(core));
            else
                Write(ctx, "text/html; charset=utf-8", Html);
        }
        catch (Exception ex)
        {
            try { ctx.Response.StatusCode = 500; Write(ctx, "text/plain", ex.Message); } catch { }
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

        var state = new
        {
            identity,
            directive,
            autonomous,
            ladder,
            champions,
            goals,
            journal,
            events,
            nodes,
            stats = new { total, discoveries, records, capabilities = core.Registry.Count },
            now = DateTime.UtcNow.ToString("HH:mm:ss"),
        };
        return JsonSerializer.Serialize(state, JsonOpts);
    }

    // The whole page — self-contained (no external dependencies, works offline). Polls /api/state.
    private const string Html = """
<!DOCTYPE html>
<html lang="en"><head>
<meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>HAL9001 — hive mission control</title>
<style>
  :root{--bg:#0a0e14;--panel:#111722;--panel2:#0d131c;--line:#1e2a3a;--txt:#c9d6e5;--dim:#6b7d93;--accent:#39d3a0;--accent2:#4aa3ff;--warn:#ffb454;--gold:#ffd166;}
  *{box-sizing:border-box;margin:0;padding:0}
  body{background:var(--bg);color:var(--txt);font:14px/1.5 ui-monospace,"Cascadia Code",Menlo,Consolas,monospace;padding:20px;}
  a{color:var(--accent2)}
  .grid{display:grid;gap:14px;grid-template-columns:repeat(auto-fit,minmax(300px,1fr));max-width:1200px;margin:0 auto;}
  header{max-width:1200px;margin:0 auto 14px;display:flex;justify-content:space-between;align-items:flex-start;flex-wrap:wrap;gap:10px;}
  h1{font-size:20px;font-weight:600;color:#fff;letter-spacing:.5px}
  .sub{color:var(--dim);font-size:12px;margin-top:3px;max-width:640px}
  .badges{display:flex;gap:8px;align-items:center;flex-wrap:wrap}
  .pill{font-size:11px;padding:4px 10px;border-radius:6px;border:1px solid var(--line);color:var(--dim)}
  .live{color:var(--accent);border-color:#143d31}
  .live .dot{display:inline-block;width:7px;height:7px;border-radius:50%;background:var(--accent);margin-right:6px;animation:pulse 1.6s infinite}
  .on{color:var(--accent2);border-color:#16324f}
  .off{color:var(--warn);border-color:#3a2e16}
  @keyframes pulse{0%,100%{opacity:1}50%{opacity:.25}}
  .panel{background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:14px 16px}
  .panel h2{font-size:12px;text-transform:uppercase;letter-spacing:1px;color:var(--dim);margin-bottom:10px;font-weight:600}
  .metrics{display:grid;grid-template-columns:repeat(4,1fr);gap:10px;max-width:1200px;margin:0 auto 14px}
  .metric{background:var(--panel2);border:1px solid var(--line);border-radius:10px;padding:12px 14px}
  .metric .v{font-size:26px;color:#fff;font-weight:600}
  .metric .l{font-size:11px;color:var(--dim);text-transform:uppercase;letter-spacing:.5px}
  .ladder{display:flex;gap:6px;flex-wrap:wrap}
  .rung{font-size:12px;padding:6px 10px;border-radius:6px;border:1px solid var(--line);color:var(--dim);background:var(--panel2)}
  .rung.done{color:var(--accent);border-color:#143d31}
  .rung.cur{color:#06121d;background:var(--accent2);border-color:var(--accent2);font-weight:600}
  table{width:100%;border-collapse:collapse;font-size:13px}
  td{padding:5px 0;border-bottom:1px solid var(--line)}
  td.r{text-align:right}
  .up{color:var(--accent)}
  .feed{max-height:340px;overflow:auto}
  .ev{display:flex;gap:8px;padding:5px 0;border-bottom:1px solid var(--line);font-size:12px}
  .ev .t{color:var(--dim);white-space:nowrap}
  .ev .k{color:var(--accent2);white-space:nowrap}
  .ev.discovery .k,.ev.discovery .s{color:var(--gold)}
  .quote{font-style:italic;color:var(--dim);border-left:2px solid var(--line);padding-left:12px;line-height:1.7}
  .empty{color:var(--dim);font-size:12px}
  .wide{grid-column:1/-1}
  footer{max-width:1200px;margin:14px auto 0;color:var(--dim);font-size:11px;text-align:center}
</style></head><body>
<header>
  <div>
    <h1 id="name">HAL9001</h1>
    <div class="sub" id="directive"></div>
  </div>
  <div class="badges">
    <span class="pill live"><span class="dot"></span>live · <span id="clock">—</span></span>
    <span class="pill" id="auto">autonomous —</span>
  </div>
</header>

<div class="metrics">
  <div class="metric"><div class="v" id="m-nodes">—</div><div class="l">active nodes</div></div>
  <div class="metric"><div class="v" id="m-records">—</div><div class="l">records set</div></div>
  <div class="metric"><div class="v" id="m-events">—</div><div class="l">life events</div></div>
  <div class="metric"><div class="v" id="m-disc">—</div><div class="l">discoveries</div></div>
</div>

<div class="grid">
  <div class="panel wide">
    <h2>size ladder · <span id="ladder-status" style="text-transform:none;letter-spacing:0;color:var(--txt)"></span></h2>
    <div class="ladder" id="ladder"></div>
  </div>
  <div class="panel">
    <h2>champions</h2>
    <table id="champs"><tbody></tbody></table>
    <div class="empty" id="champs-empty" style="display:none">no records yet — run the swarm with `autonomous on`.</div>
  </div>
  <div class="panel">
    <h2>self-set goals</h2>
    <div id="goals"></div>
  </div>
  <div class="panel wide">
    <h2>live activity</h2>
    <div class="feed" id="feed"></div>
  </div>
  <div class="panel wide">
    <h2>latest journal</h2>
    <div class="quote" id="journal">—</div>
  </div>
</div>
<footer>HAL9001 · reading the shared hive · refreshing every 2.5s</footer>

<script>
const $=id=>document.getElementById(id);
const esc=s=>(s||"").replace(/[&<>]/g,c=>({"&":"&amp;","<":"&lt;",">":"&gt;"}[c]));
function score(c){return c.metric==="muls"?Math.round(c.score)+" muls":c.score.toFixed(2)+" ms";}
async function tick(){
  let s; try{ s=await (await fetch("/api/state")).json(); }catch(e){ return; }
  $("clock").textContent=s.now||"—";
  if(s.identity){ $("name").textContent=s.identity.name+" "; $("name").innerHTML=esc(s.identity.name)+' <span style="color:var(--dim);font-size:12px;font-weight:400">· '+esc(s.identity.concept||"")+'</span>'; }
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
}
tick(); setInterval(tick,2500);
</script>
</body></html>
""";
}
