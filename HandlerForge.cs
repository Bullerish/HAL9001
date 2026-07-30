namespace HAL9001;

/// <summary>
/// Resolve live handlers by name from generated composite code (same process).
/// Wired in <see cref="HandlerForge.Register"/>.
/// </summary>
public static class HandlerBridge
{
    public static HandlerRegistry? Registry;

    public static IHandler? Resolve(string name)
    {
        if (Registry is null) return null;
        return Registry.TryGet(name, out IHandler h) ? h : null;
    }
}

/// <summary>
/// LLM-FREE HANDLER FORGE (issues #20, #21).
///
/// Continuous self-improvement of the capability catalog without calling a language model:
///   1. Stress existing Stable handlers (determinism, boundaries, light fuzz).
///   2. Multi-node quorum on compose adoption (HAL_FORGE_QUORUM, default 1).
///   3. Propose + materialize type-compatible A→B compositions not yet in the registry.
///   4. Approved composites are compiled, re-stressed, then written to handlers/ + git push.
///
/// Call <see cref="Register"/> once after AgentCore is up (agent + swarm).
/// </summary>
public static class HandlerForge
{
    // ── background forge loop (started via Register from agent/swarm startup) ───────────
    static AgentCore? _core;
    static int _started; // 0/1 via Interlocked
    static DateTime _lastTick = DateTime.MinValue;
    static double _intervalSecs = 180.0;
    static bool _enabled = true;
    static int _quorum = 1;
    static readonly HashSet<string> _sessionTried = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> _sessionApproved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Call once after AgentCore is up. Starts a free-CPU loop that stresses Stable handlers
    /// and adopts composites. Safe to call multiple times.
    /// Env: HAL_FORGE (default on), HAL_FORGE_SECS (default 180 × HAL_PACE),
    /// HAL_FORGE_QUORUM (default 1 — set 2+ so multiple nodes must propose the same compose).
    /// </summary>
    public static void Register(AgentCore core)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
        HandlerBridge.Registry = core.Registry;
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
            if (int.TryParse(Environment.GetEnvironmentVariable("HAL_FORGE_QUORUM"), out int q) && q > 0)
                _quorum = Math.Clamp(q, 1, 16);
        }
        if (!_enabled) { LiveLog.Append("> forge: disabled (HAL_FORGE=off)"); return; }
        if (System.Threading.Interlocked.Exchange(ref _started, 1) == 1) return;
        _ = Task.Run(BackgroundLoopAsync);
        LiveLog.Append($"> forge: background loop on (every {_intervalSecs:0}s, quorum={_quorum})");
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

    /// <summary>
    /// One forge tick: stress Stable caps, propose A→B composites, materialize + re-stress,
    /// and (after quorum) push approved source to git.
    /// </summary>
    public static async Task TickAsync(AgentCore core, int limit = 3)
    {
        HandlerBridge.Registry = core.Registry;
        var all = core.Registry.Catalog().ToList();
        if (all.Count == 0)
        {
            LiveLog.Append("> forge: no capabilities registered yet");
            return;
        }

        // Round-robin-ish: pick different Stable caps each tick via time-based offset.
        var stableAll = all.Where(c => c.Stability == StabilityKind.Stable).ToList();
        int offset = (int)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond) % Math.Max(1, stableAll.Count);
        var stable = stableAll.Skip(offset).Concat(stableAll.Take(offset)).Take(limit).ToList();

        var verified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            if (report.Ok) verified.Add(cap.Name);
        }

        // Only compose from caps that just passed (or are Stable and we didn't retest this tick).
        var ideas = ProposeComposites(all, max: 3);
        foreach (var idea in ideas)
        {
            string line = $"forge compose?: {idea.ProposedName} — {idea.Rationale}";
            LiveLog.Append("> " + line);
            try { await core.Events.AppendAsync("forge-compose-idea", line, idea.ProposedName); } catch { }

            // Prefer pairs we just verified; still allow if both exist Stable.
            if (!core.Registry.TryGetCapability(idea.First.Name, out _) ||
                !core.Registry.TryGetCapability(idea.Second.Name, out _))
                continue;

            await TryAdoptCompositeAsync(core, idea);
        }
    }

    /// <summary>
    /// Materialize a composite IHandler (no LLM), stress it, vote via hive events, and on
    /// quorum write handlers/ + git push so the whole swarm picks it up.
    /// </summary>
    public static async Task<bool> TryAdoptCompositeAsync(AgentCore core, CompositeIdea idea)
    {
        if (_sessionApproved.Contains(idea.ProposedName)) return false;
        if (core.Registry.TryGetCapability(idea.ProposedName, out _)) return false;

        // Cast a vote every time we rediscover the idea (multi-node quorum counts distinct actors).
        try
        {
            await core.Events.AppendAsync(
                "forge-compose-vote",
                $"vote for {idea.ProposedName} ({idea.First.Name}→{idea.Second.Name})",
                idea.ProposedName);
        }
        catch { }

        int votes = await CountVotesAsync(core, idea.ProposedName);
        LiveLog.Append($"> forge vote {idea.ProposedName}: {votes}/{_quorum}");
        if (votes < _quorum)
            return false;

        if (!_sessionTried.Add(idea.ProposedName))
            return false; // already tried compile/adopt this process after quorum

        string example = string.IsNullOrWhiteSpace(idea.First.ExampleRequest)
            ? "1"
            : idea.First.ExampleRequest.Trim();
        string desc =
            $"LLM-free forge composite: run '{idea.First.Name}' then '{idea.Second.Name}'. {idea.Rationale}";
        string source = EmitComposeSource(idea);
        CapType inT = idea.First.InputType;
        CapType outT = idea.Second.OutputType;

        if (!RuntimeCompiler.TryCompileAndLoad(
                idea.ProposedName, desc, example, source, core.Registry,
                out IHandler? handler, out string? err, inT, outT, StabilityKind.Stable)
            || handler is null)
        {
            LiveLog.Append($"!! forge adopt failed compile: {idea.ProposedName} — {Preview(err ?? "?")}");
            try { await core.Events.AppendAsync("forge-compose-reject", "compile failed", idea.ProposedName); } catch { }
            return false;
        }

        if (!core.Registry.TryGetCapability(idea.ProposedName, out Capability cap))
            return false;

        SuiteReport report = await StressAsync(core, cap);
        LiveLog.Append((report.Ok ? "OK " : "!! ") + "forge adopt " + report.Summary);
        if (!report.Ok)
        {
            core.Registry.Remove(idea.ProposedName);
            try { await core.Events.AppendAsync("forge-compose-reject", report.Summary, idea.ProposedName); } catch { }
            return false;
        }

        bool pushed = PersistToGit(core, idea.ProposedName, desc, example, source, inT, outT);
        _sessionApproved.Add(idea.ProposedName);
        string msg = pushed
            ? $"forge APPROVED + git: {idea.ProposedName}"
            : $"forge APPROVED (memory only): {idea.ProposedName}";
        LiveLog.Append("> " + msg);
        try { await core.Events.AppendAsync("forge-compose-approved", msg, idea.ProposedName); } catch { }
        return true;
    }

    static async Task<int> CountVotesAsync(AgentCore core, string proposedName)
    {
        try
        {
            var recent = await core.Events.RecentAsync(200);
            var actors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in recent)
            {
                if (!string.Equals(e.Kind, "forge-compose-vote", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.Equals(e.Ref, proposedName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.IsNullOrWhiteSpace(e.Actor))
                    actors.Add(e.Actor);
            }
            // Always count this node at least once even if the write hasn't round-tripped yet.
            if (!string.IsNullOrWhiteSpace(core.Events.Actor))
                actors.Add(core.Events.Actor);
            return Math.Max(1, actors.Count);
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>
    /// Thin generated IHandler that chains two live registry entries via <see cref="HandlerBridge"/>.
    /// Survives pull+load on other nodes because HandlerBridge is wired at Register time.
    /// </summary>
    static string EmitComposeSource(CompositeIdea idea)
    {
        string cls = "ForgeCompose_" + Slug(idea.ProposedName).Replace("-", "_");
        if (cls.Length > 0 && char.IsDigit(cls[0])) cls = "C_" + cls;
        // Escape for string literals
        string a = idea.First.Name.Replace("\\", "\\\\").Replace("\"", "\\\"");
        string b = idea.Second.Name.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $$"""
using System;
using HAL9001;

public class {{cls}} : IHandler
{
    public string Handle(string input)
    {
        var first = HandlerBridge.Resolve("{{a}}");
        var second = HandlerBridge.Resolve("{{b}}");
        if (first is null) return "(compose: missing '{{a}}')";
        if (second is null) return "(compose: missing '{{b}}')";
        string mid = first.Handle(input ?? "") ?? "";
        return second.Handle(mid) ?? "";
    }
}
""";
    }

    /// <summary>Write header + source under handlers/ and commit+push (no LLM required).</summary>
    static bool PersistToGit(
        AgentCore core,
        string name,
        string description,
        string exampleRequest,
        string source,
        CapType inputType,
        CapType outputType)
    {
        GitSync? git = core.Git;
        if (git is null)
        {
            LiveLog.Append("> forge: no git repo — composite kept in memory only");
            return false;
        }
        try
        {
            Directory.CreateDirectory(git.HandlersDirectory);
            string fileBase = "ForgeCompose_" + Slug(name).Replace("-", "_");
            string unique = Guid.NewGuid().ToString("N")[..8];
            string fileName = $"{fileBase}_{unique}.cs";
            string fullPath = Path.Combine(git.HandlersDirectory, fileName);
            string header =
                $"// hal9001:name={name}\n" +
                $"// hal9001:description={OneLine(description)}\n" +
                $"// hal9001:request={OneLine(exampleRequest)}\n" +
                $"// hal9001:intype={CapTypes.Name(inputType)}\n" +
                $"// hal9001:outtype={CapTypes.Name(outputType)}\n" +
                $"// hal9001:stability=Stable\n" +
                $"// hal9001:origin=forge-llmfree\n";
            File.WriteAllText(fullPath, header + source);
            LiveLog.Append($"> forge wrote handlers/{fileName}");
            if (git.CommitAndPushFile(fullPath, $"forge: approve composite {name}"))
            {
                LiveLog.Append($"> forge pushed handlers/{fileName}");
                return true;
            }
            LiveLog.Append("!! forge git push failed (file on disk)");
            return false;
        }
        catch (Exception ex)
        {
            LiveLog.Append($"!! forge persist: {ex.Message}");
            return false;
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
        => s.Length <= 48 ? s : s[..48] + "…";

    private static string OneLine(string text) =>
        text.Replace("\r", " ").Replace("\n", " ").Trim();
}
