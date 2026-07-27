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
/// This is the mechanical core only. Swarm message fan-out / git-on-approve wiring lives in
/// SwarmAgent + AgentCore call sites (follow-up commits on the same track).
/// </summary>
public static class HandlerForge
{
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

        // 1) Example smoke
        string example = string.IsNullOrWhiteSpace(cap.ExampleRequest) ? "1" : cap.ExampleRequest.Trim();
        var smoke = await RunOnceAsync(core, cap, example, timeoutMs);
        ran++;
        if (smoke.Crashed) { crashed++; notes.Add("example crashed: " + (smoke.Error ?? "?")); }
        else if (string.IsNullOrWhiteSpace(smoke.Output)) { notes.Add("example returned empty"); }
        else passed++;

        // 2) Determinism (Stable only)
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

        // 3) Boundary / fuzz — must not crash the process
        foreach (string bad in BoundaryInputs(example))
        {
            var r = await RunOnceAsync(core, cap, bad, timeoutMs);
            ran++;
            if (r.Crashed) { crashed++; notes.Add($"crash on boundary '{Preview(bad)}'"); }
            else passed++; // typed error / empty is fine; crash is not
        }

        return new SuiteReport(cap.Name, ran, passed, crashed, nondet, notes);
    }

    /// <summary>
    /// Propose novel A→B composites from type-compatible Stable pairs not already in the registry.
    /// Does not synthesize source — only names candidates the swarm can later wire / verify.
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
                if (a.OutputType != b.InputType) continue; // type chain
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
            // Handlers are sync IHandler.Handle — run on the thread pool with a hard timeout so a
            // wedged handler cannot stall the forge tick (or the matmul race that shares the process).
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
            yield return example[..Math.Max(1, example.Length / 2)]; // truncate
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
