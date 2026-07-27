namespace HAL9001;

/// <summary>
/// LLM-FREE HANDLER FORGE (issues #20, #21).
///
/// Continuous self-improvement of the capability catalog without calling a language model:
///   1. Stress existing Stable handlers (determinism, boundaries, light fuzz).
///   2. Multi-node consensus is coordinated by the swarm (this module is the local suite).
///   3. Propose type-compatible A→B compositions that are not yet in the registry.
///   4. Approved sources are persisted via <see cref="HandlerGenerator.PersistShared"/> (git).
///
/// Call <see cref="Register"/> once after AgentCore is up (agent + swarm). The background
/// loop stresses handlers and proposes composites; logs to LiveLog for the CRT.
/// </summary>
public static class HandlerForge
{
    // ── background forge loop (started via Register from agent/swarm startup) ───────────
    static AgentCore? _core;
    static int _started; // 0/1 via Interlocked
    static DateTime _lastTick = DateTime.MinValue;
    static double _intervalSecs = 180.0;
    static bool _enabled = true;

    /// <summary>
    /// Call once after AgentCore is constructed and hive is up. Starts a free-CPU loop that
    /// stresses Stable handlers and proposes composites. Safe to call multiple times.
    /// HAL_FORGE=0/off disables; HAL_FORGE_SECS sets interval (default 180).
    /// </summary>
    public static void Register(AgentCore core)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
        {
            string? f = Environment.GetEnvironmentVariable("HAL_FORGE")?.Trim().ToLowerInvariant();
            if (f is "0" or "off" or "false" or "no") _enabled = false;
            if (double.TryParse(Environment.GetEnvironmentVariable("HAL_FORGE_SECS"), out double fs) && fs > 0)
                _intervalSecs = Math.Clamp(fs, 30, 3600);
            string? paceEnv = Environment.GetEnvironmentVariable("HAL_PACE");
            double pace = 1.0;
            if (string.Equals(paceEnv, "slow", StringComparison.OrdinalIgnoreCase)) pace = 6.0;
            else if (double.TryParse(paceEnv, out double pv) && pv > 0) pace = pv;
            _intervalSecs *= pace;
        }
        if (!_enabled) { LiveLog.Append("> forge: disabled (HAL_FORGE=off)"); return; }
        if (Interlocked.Exchange(ref _started, 1) == 1) return; // already running
        _ = Task.Run(BackgroundLoopAsync);
        LiveLog.Append($"> forge: background loop on (every {_intervalSecs:0}s)");
    }

    static async Task BackgroundLoopAsync()
    {
        while (_enabled)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(10, _intervalSecs / 6))); }
            catch { break; }
            var core = _core;
            if (core is null || !core.HasHive) continue;
            bool isAuto;
            try { isAuto = await core.IsAutonomousAsync(); } catch { continue; }
            if (!isAuto) continue;
            if ((DateTime.UtcNow - _lastTick).TotalSeconds < _intervalSecs) continue;
            _lastTick = DateTime.UtcNow;
            try { await TickAsync(core, limit: 3); }
            catch (Exception ex) { LiveLog.Append($"  forge tick failed: {ex.Message}"); }
        }
    }

    private static readonly System.Globalization.CultureInfo Inv =
        System.Globalization.CultureInfo.InvariantCulture;

    public sealed record VectorResult(string Input, string? Output, bool Crashed, string? Error, long Ms);

    public sealed record SuiteReport(
        string Name,
        int Ran,
        int Passed,
        int Crashed,
        int NonDeterministic,
        IReadOnlyList<string> Notes)
    {
        public bool Ok => Crashed == 0 && NonDeterministic == 0 && Passed == Ran && Ran > 0;
        public string Summary =>
            $"{Name}: {(Ok ? "PASS" : "FAIL")} ran={Ran} pass={Passed} crash={Crashed} nondet={NonDeterministic}"
            + (Notes.Count == 0 ? "" : " — " + string.Join("; ", Notes.Take(3)));
    }

    public sealed record CompositeIdea(Capability First, Capability Second, string ProposedName, string Rationale);

    /// <summary>Run the local mechanical suite against one capability (no LLM, no network).</summary>
    public static async Task<SuiteReport> StressAsync(
        AgentCore core,
        Capability cap,
        int determinismTrials = 3,
        int timeoutMs = 1500)
    {
        var notes = new List<string>();
        int ran = 0, passed = 0, crashed = 0, nondet = 0;

        string example = string.IsNullOrWhiteSpace(cap.ExampleRequest) ? "1" : cap.ExampleRequest.Trim();
        var smoke = await RunOnceAsync(core, cap, example, timeoutMs);
        ran++;
        if (smoke.Crashed) { crashed++; notes.Add("example crashed: " + (smoke.Error ?? "?")); }
        else if (string.IsNullOrWhiteSpace(smoke.Output)) { notes.Add("example returned empty"); }
        else passed++;

        if (cap.Stability == StabilityKind.Stable && !smoke.Crashed)
        {
            string? first = smoke.Output;
            for (int i = 1; i < determinismTrials; i++)
            {
                var again = await RunOnceAsync(core, cap, example, timeoutMs);
                ran++;
                if (again.Crashed) { crashed++; notes.Add("determinism trial crashed"); break; }
                if (!string.Equals(first, again.Output, StringComparison.Ordinal))
                {
                    nondet++;
                    notes.Add("non-deterministic on example");
                    break;
                }
                passed++;
            }
        }

        foreach (string bad in BoundaryInputs(example))
        {
            var r = await RunOnceAsync(core, cap, bad, timeoutMs);
            ran++;
            if (r.Crashed) { crashed++; notes.Add($"crash on boundary '{Preview(bad)}'"); }
            else passed++;
        }

        return new SuiteReport(cap.Name, ran, passed, crashed, nondet, notes);
    }

    /// <summary>
    /// Propose novel A→B composites from type-compatible Stable pairs not already in the registry.
    /// </summary>
    public static IReadOnlyList<CompositeIdea> ProposeComposites(IReadOnlyList<Capability> caps, int max = 8)
    {
        var stable = caps.Where(c => c.Stability == StabilityKind.Stable).ToList();
        var names = new HashSet<string>(caps.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        var ideas = new List<CompositeIdea>();

        foreach (var a in stable)
        {
            foreach (var b in stable)
            {
                if (ReferenceEquals(a, b)) continue;
                if (a.OutputType != b.InputType) continue;
                string proposed = $"compose-{Slug(a.Name)}-then-{Slug(b.Name)}";
                if (names.Contains(proposed)) continue;
                if (ideas.Any(i => i.ProposedName == proposed)) continue;
                ideas.Add(new CompositeIdea(
                    a, b, proposed,
                    $"chain {a.Name} ({CapTypes.Name(a.OutputType)}) → {b.Name}; solves a path not listed as its own capability"));
                if (ideas.Count >= max) return ideas;
            }
        }
        return ideas;
    }

    /// <summary>One forge tick: stress up to <paramref name="limit"/> Stable caps; log via LiveLog.</summary>
    public static async Task TickAsync(AgentCore core, int limit = 3)
    {
        var all = core.Registry.Catalog().ToList();
        if (all.Count == 0)
        {
            LiveLog.Append("> forge: no capabilities registered yet");
            return;
        }

        var stable = all.Where(c => c.Stability == StabilityKind.Stable).Take(limit).ToList();
        foreach (var cap in stable)
        {
            SuiteReport report = await StressAsync(core, cap);
            LiveLog.Append((report.Ok ? "OK " : "!! ") + "forge " + report.Summary);
            try
            {
                await core.Events.AppendAsync(
                    report.Ok ? "handler-verified" : "handler-broken",
                    report.Summary,
                    cap.Name);
            }
            catch { }
        }

        var ideas = ProposeComposites(all, max: 3);
        foreach (var idea in ideas)
        {
            string line = $"forge compose?: {idea.ProposedName} — {idea.Rationale}";
            LiveLog.Append("> " + line);
            try { await core.Events.AppendAsync("forge-compose-idea", line, idea.ProposedName); } catch { }
        }
    }

    private static async Task<VectorResult> RunOnceAsync(AgentCore core, Capability cap, string input, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            Task<string> task = Task.Run(() =>
            {
                try { return cap.Handler.Handle(input) ?? ""; }
                catch (Exception ex) { throw new InvalidOperationException(ex.GetType().Name + ": " + ex.Message, ex); }
            });
            var done = await Task.WhenAny(task, Task.Delay(timeoutMs));
            if (done != task)
                return new VectorResult(input, null, true, "timeout", sw.ElapsedMilliseconds);
            string output = await task;
            return new VectorResult(input, output, false, null, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            string msg = ex is InvalidOperationException && ex.InnerException is not null
                ? ex.Message
                : ex.GetType().Name + ": " + ex.Message;
            return new VectorResult(input, null, true, msg, sw.ElapsedMilliseconds);
        }
    }

    private static IEnumerable<string> BoundaryInputs(string example)
    {
        yield return "";
        yield return " ";
        yield return "\t";
        yield return "<<<not-a-valid-payload>>>";
        if (example.Length > 0)
        {
            yield return example[..Math.Max(1, example.Length / 2)];
            char[] chars = example.ToCharArray();
            if (chars.Length > 0)
            {
                chars[0] = chars[0] == 'x' ? 'y' : 'x';
                yield return new string(chars);
            }
        }
    }

    private static string Slug(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (c is '-' or '_' || char.IsWhiteSpace(c)) sb.Append('-');
        }
        string s = sb.ToString().Trim('-');
        return string.IsNullOrEmpty(s) ? "cap" : s;
    }

    private static string Preview(string s)
        => s.Length <= 24 ? s : s[..24] + "…";
}
