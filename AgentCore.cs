using System.Text.Json;

namespace HAL9001;

/// <summary>
/// The ONE shared answer path for the whole project. Both the two-node phase-1 agent
/// (<see cref="AgentRepl"/>) and the swarm agent (<see cref="SwarmAgent"/>) route their
/// "turn this question into an answer" logic through here, so there is a single implementation
/// of the LLM-as-toolsmith pipeline instead of two copies that drift apart.
///
/// What lives here (the answer path):
///   • the handler REGISTRY (the capabilities this node knows),
///   • GitHub sync (pull shared handlers on startup; push is done inside generation),
///   • the THREE-WAY CLASSIFIER (use existing / commission new / decline),
///   • runtime COMPILATION + LLM GENERATION of a new general capability,
///   • RUNNING the chosen capability under a timeout, never crashing on a bad handler,
///   • one SERIALIZATION gate so concurrent callers (a REPL thread + a socket thread, or
///     several swarm message tasks) never corrupt the shared registry/generator.
///
/// What does NOT live here: any coordination. Coordinator election, quorum, heartbeats,
/// assignment, in-flight recovery, and message routing stay in <see cref="SwarmAgent"/>; the
/// two-node host/join transport + one-round loop guard stay in <see cref="AgentRepl"/>. This
/// class only knows how to answer a question — it knows nothing about peers.
/// </summary>
public sealed class AgentCore
{
    private readonly AnthropicClient? _client;
    private readonly CapabilityRouter? _router;
    private readonly HandlerGenerator? _generator;

    // One gate for the whole answer path. The registry and generator are shared mutable state;
    // serializing answer production keeps two concurrent callers from interleaving a half-built
    // handler. (This replaces AgentRepl's `requestGate` and SwarmAgent's `answerGate`.)
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The capabilities this node knows. Shared by callers for catalog/count/lookup.</summary>
    public HandlerRegistry Registry { get; } = new();

    /// <summary>The git repo handlers are synced through, or null if we're not inside one.</summary>
    public GitSync? Git { get; }

    /// <summary>True when an API key was available — i.e. we can actually route + generate.</summary>
    public bool HasLlm => _router is not null && _generator is not null;

    public AgentCore(AnthropicClient? client)
    {
        _client = client;
        Git = GitSync.Discover();
        if (client is not null)
        {
            _generator = new HandlerGenerator(client, Registry, Git);
            _router = new CapabilityRouter(client, Registry);
        }
    }

    /// <summary>
    /// Pull handlers other instances pushed and compile+register them. No-op without a repo.
    /// Returns how many handlers are loaded afterwards (for the startup banner).
    /// </summary>
    public int LoadSharedHandlers()
    {
        if (Git is not null)
        {
            Git.Pull();
            HandlerLoader.LoadAll(Git.HandlersDirectory, Registry);
        }
        return Registry.Count;
    }

    /// <summary>
    /// The answer path: ROUTE the input (use existing / commission new / decline), then either
    /// reply conversationally (decline) or RUN the chosen capability and return its output. The
    /// LLM only recognizes/commissions — the answer is always the output of running compiled
    /// code. Used identically by a locally-typed request and a peer/swarm question.
    ///
    /// Callers must check <see cref="HasLlm"/> first; this assumes a key is present.
    /// </summary>
    public async Task<AnswerResult> AnswerAsync(string request, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            RouteDecision decision = await _router!.RouteAsync(request, ct);

            // NOT A TASK → conversational reply; touch nothing in the compile/push pipeline.
            if (decision.Action == RouteAction.Decline)
                return AnswerResult.Declined(decision.Reply);

            IHandler? handler;
            string usedName;
            CapType inType;   // declared input type of the chosen capability — for the boundary check
            if (decision.Action == RouteAction.UseExisting && Registry.TryGetCapability(decision.Name, out Capability cap))
            {
                handler = cap.Handler;
                usedName = decision.Name;
                inType = cap.InputType;
                Console.WriteLine($"  (using capability '{usedName}' [{CapTypes.Name(cap.InputType)}→{CapTypes.Name(cap.OutputType)}])");
            }
            else
            {
                // CreateNew — or a UseExisting that named something we don't actually have
                // (an LLM slip): commission a general capability either way, with declared types.
                string capName = decision.Name.Length > 0 ? decision.Name : "capability";
                string capDesc = decision.Description.Length > 0 ? decision.Description : request;
                usedName = capName;
                inType = decision.InputType;
                Console.WriteLine($"  (commissioning '{capName}' [{CapTypes.Name(decision.InputType)}→{CapTypes.Name(decision.OutputType)}]: {capDesc})");
                GeneratedHandler? gen;
                try
                {
                    gen = await _generator!.GenerateAsync(capName, capDesc, request, ct, persist: true, decision.InputType, decision.OutputType);
                }
                catch (Exception ex)
                {
                    return AnswerResult.GenerationFailed($"(generation failed: {ex.Message})");
                }
                if (gen is null)
                    return AnswerResult.GenerationFailed("(couldn't build a working handler)");
                handler = gen.Handler;
            }

            // Boundary parse-check (typed-capabilities rung): if the input can't possibly hold the
            // declared input type, return a clean typed error instead of running the handler on garbage.
            if (!CapTypes.Matches(inType, request))
                return AnswerResult.Answered(CapTypes.Mismatch(inType, request), usedName);

            // Run the compiled capability with a timeout so a hung network call in generated
            // code can't freeze the agent, and catch any runtime throw — never crash.
            try
            {
                string result = await Task.Run(() => handler!.Handle(request), ct)
                                          .WaitAsync(TimeSpan.FromSeconds(30), ct);
                return AnswerResult.Answered(result, usedName);
            }
            catch (TimeoutException)
            {
                return AnswerResult.Answered("(the capability took too long to run)", usedName);
            }
            catch (Exception ex)
            {
                return AnswerResult.Answered($"(the capability errored at runtime: {ex.GetBaseException().Message})", usedName);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── rung 5a: deliberation support ────────────────────────────────────────────────────

    /// <summary>
    /// Prepare a deliberation (rungs 5a/5b + typing): in ONE LLM call, infer the capability's
    /// declared INPUT and OUTPUT types from the question AND generate a few test cases consistent
    /// with those types. The coordinator does this once, then fixes the same types + tests for every
    /// competing member — so candidates are comparable and the tests match the declared types.
    /// Best-effort: String→String with no tests if there's no key or the reply doesn't parse.
    /// </summary>
    public async Task<DeliberationSpec> PrepareDeliberationAsync(string question, CancellationToken ct = default)
    {
        if (_client is null) return new DeliberationSpec(CapType.String, CapType.String, Array.Empty<TestCase>());

        const string sys = """
            You set up a competition to build a tool that answers questions like the one given.
            First decide the tool's INPUT and OUTPUT types from this fixed set:
              String (free-form text), Int (a whole number), Number (integer or decimal),
              Bool (yes/no), Date (a calendar date).
            Then write 2-3 SMALL test cases consistent with those types.
            Output ONLY this JSON (no prose, no fences):
              {"inputType":"<String|Int|Number|Bool|Date>",
               "outputType":"<String|Int|Number|Bool|Date>",
               "tests":[{"input":"<a concrete request>","expected":"<key substring the right answer must contain>"}]}
            Keep each "expected" short and exact.
            """;
        try
        {
            string raw = await _client.CompleteAsync(sys, $"Question: \"{question}\"\nJSON:", ct);
            using JsonDocument doc = JsonDocument.Parse(StripFences(raw));
            JsonElement root = doc.RootElement;
            CapType inT = CapTypes.Parse(root.TryGetProperty("inputType", out var i) ? i.GetString() : null);
            CapType outT = CapTypes.Parse(root.TryGetProperty("outputType", out var o) ? o.GetString() : null);
            var list = new List<TestCase>();
            if (root.TryGetProperty("tests", out JsonElement tests) && tests.ValueKind == JsonValueKind.Array)
                foreach (JsonElement e in tests.EnumerateArray())
                {
                    string input = e.TryGetProperty("input", out var ii) ? ii.GetString() ?? "" : "";
                    string expected = e.TryGetProperty("expected", out var xx) ? xx.GetString() ?? "" : "";
                    if (input.Length > 0 && expected.Length > 0) list.Add(new TestCase(input, expected));
                    if (list.Count == 4) break;
                }
            return new DeliberationSpec(inT, outT, list);
        }
        catch { return new DeliberationSpec(CapType.String, CapType.String, Array.Empty<TestCase>()); }
    }

    /// <summary>
    /// Produce a COMPETING candidate (rung 5a): route the request, reuse a matching handler or
    /// commission a fresh one — but NEVER push it (persist:false), so N nodes generating rivals
    /// don't spam the repo. The declared input/output types are FIXED by the coordinator so every
    /// candidate targets the same contract. Then run the chosen handler on the question (the
    /// candidate answer) and on each test case (pass/fail), with the boundary type-check applied.
    /// </summary>
    public async Task<Candidate> ProduceCandidateAsync(
        string request, CapType inputType, CapType outputType, IReadOnlyList<TestCase> tests, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            RouteDecision decision = await _router!.RouteAsync(request, ct);
            if (decision.Action == RouteAction.Decline)
                return new Candidate(CandidateStatus.Declined, decision.Reply, null, null, "", Array.Empty<TestResult>());

            IHandler? handler;
            string usedName;
            string usedDesc;
            string source = "";   // the generated source (empty for an existing handler) — for the winner push
            if (decision.Action == RouteAction.UseExisting && Registry.TryGet(decision.Name, out handler))
            {
                usedName = decision.Name;
                usedDesc = decision.Description;
                Console.WriteLine($"  (candidate: using capability '{usedName}')");
            }
            else
            {
                string capName = decision.Name.Length > 0 ? decision.Name : "capability";
                string capDesc = decision.Description.Length > 0 ? decision.Description : request;
                usedName = capName;
                usedDesc = capDesc;
                Console.WriteLine($"  (candidate: commissioning '{capName}' [{CapTypes.Name(inputType)}→{CapTypes.Name(outputType)}] — held locally, not pushed)");
                GeneratedHandler? gen;
                try { gen = await _generator!.GenerateAsync(capName, capDesc, request, ct, persist: false, inputType, outputType); }
                catch (Exception ex) { return new Candidate(CandidateStatus.GenerationFailed, $"(generation failed: {ex.Message})", capName, capDesc, "", Array.Empty<TestResult>()); }
                if (gen is null) return new Candidate(CandidateStatus.GenerationFailed, "(couldn't build a working handler)", capName, capDesc, "", Array.Empty<TestResult>());
                handler = gen.Handler;
                source = gen.Source;
            }

            // Boundary type-check on the question itself; a clean typed error if it doesn't fit.
            string answer = CapTypes.Matches(inputType, request)
                ? await RunHandlerAsync(handler!, request)
                : CapTypes.Mismatch(inputType, request);
            var results = new List<TestResult>(tests.Count);
            foreach (TestCase tc in tests)
            {
                string got = CapTypes.Matches(inputType, tc.Input)
                    ? await RunHandlerAsync(handler!, tc.Input)
                    : CapTypes.Mismatch(inputType, tc.Input);
                bool pass = got.Contains(tc.Expected, StringComparison.OrdinalIgnoreCase);
                results.Add(new TestResult(tc.Input, tc.Expected, got, pass));
            }
            return new Candidate(CandidateStatus.Ok, answer, usedName, usedDesc, source, results);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Push the deliberation WINNER (rung 5b): persist + commit + push exactly the one winning
    /// candidate's source — with its declared types in the header — so it becomes the swarm's
    /// canonical handler. No-op without a key/generator.
    /// </summary>
    public bool TryPersistWinner(string name, string description, string exampleRequest, string source,
        CapType inputType, CapType outputType)
    {
        if (_generator is null || source.Length == 0) return false;
        _generator.PersistShared(name, description, exampleRequest, source, inputType, outputType);
        return true;
    }

    // Run a handler under the same 30s guard used everywhere, turning any throw/timeout into a
    // string so a bad candidate never crashes the node.
    private static async Task<string> RunHandlerAsync(IHandler handler, string input)
    {
        try { return await Task.Run(() => handler.Handle(input)).WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { return "(the capability took too long to run)"; }
        catch (Exception ex) { return $"(the capability errored at runtime: {ex.GetBaseException().Message})"; }
    }

    // ── composition rung: linear chains of EXISTING typed capabilities ───────────────────

    /// <summary>
    /// Answer a possibly-COMPOSITE question by chaining EXISTING typed capabilities. Steps:
    ///   1. DECOMPOSE (LLM, given the real catalog with types): single / chain / none.
    ///   2. If not a chain → answer it as a single capability (unchanged path), labeled.
    ///   3. RESOLVE every named step against the registry — existing-only, NEVER generate.
    ///   4. DISPLAY the plan before running anything.
    ///   5. TYPE-CHECK every seam (step N's output type == step N+1's input type) — reject hard.
    ///   6. EXECUTE in order, feeding output→input with a per-step boundary check; any step that
    ///      fails (missing/type-mismatch/runtime) fails the WHOLE composition, naming the step.
    /// Linear + existing-only this rung: no auto-generation of missing links, no nested chains.
    /// </summary>
    public async Task<CompositionResult> ComposeAsync(string question, CancellationToken ct = default)
    {
        if (!HasLlm) return CompositionResult.Failed("(no API key — cannot decompose)");

        IReadOnlyList<Capability> catalog = Registry.Catalog();
        DecompositionPlan plan = await DecomposeAsync(question, catalog, ct);

        // Not genuinely composite → answer as a single capability via the normal (unchanged) path.
        // This is how a simple question is NOT spuriously decomposed: a 0/1-step plan falls through.
        if (plan.Mode != "chain" || plan.Steps.Count < 2)
        {
            if (plan.Mode == "none" && plan.Steps.Count == 0)
                return CompositionResult.Failed($"cannot compose: {(plan.Reason.Length > 0 ? plan.Reason : "no suitable capabilities")}");
            AnswerResult single = await AnswerAsync(question, ct);
            return CompositionResult.NotComposite(single.Text);
        }

        // RESOLVE each step against the registry. A named-but-missing capability is a CLEAN failure
        // — we do NOT generate it. >>> auto-generating a missing link would attach HERE (later rung). <<<
        var caps = new List<Capability>(plan.Steps.Count);
        foreach (string name in plan.Steps)
        {
            if (!Registry.TryGetCapability(name, out Capability c))
                return CompositionResult.Failed($"cannot compose: no capability '{name}' available");
            // >>> nested composition (a step that is itself a chain) would attach HERE (later rung). <<<
            caps.Add(c);
        }

        // DISPLAY the plan BEFORE executing — so decomposition and execution are separately observable.
        string planLine = "[composition] plan: " +
            string.Join(" → ", caps.Select(c => $"{c.Name} [{CapTypes.Name(c.InputType)}→{CapTypes.Name(c.OutputType)}]"));
        Console.WriteLine(planLine);

        // TYPE-CHECK every seam BEFORE running anything. Exact match (no coercion this rung).
        for (int i = 0; i + 1 < caps.Count; i++)
            if (caps[i].OutputType != caps[i + 1].InputType)
                return CompositionResult.Failed(
                    $"cannot compose: '{caps[i].Name}' outputs {CapTypes.Name(caps[i].OutputType)} " +
                    $"but '{caps[i + 1].Name}' expects {CapTypes.Name(caps[i + 1].InputType)}");

        // EXECUTE: step 1 reads the raw question; each step's output feeds the next.
        string current = question;
        for (int i = 0; i < caps.Count; i++)
        {
            Capability c = caps[i];
            if (!CapTypes.Matches(c.InputType, current))
                return CompositionResult.Failed($"composition failed at step {i + 1} ('{c.Name}'): {CapTypes.Mismatch(c.InputType, current)}");
            (bool ok, string outp) = await RunStepAsync(c.Handler, current);
            if (!ok)
                return CompositionResult.Failed($"composition failed at step {i + 1} ('{c.Name}'): {outp}");
            Console.WriteLine($"[composition] step {i + 1} {c.Name}: \"{Trunc(current)}\" → \"{Trunc(outp)}\"");
            current = outp;
        }
        return CompositionResult.Chain(current, caps.Select(c => c.Name).ToList());
    }

    // The judgment step: classify the question against the REAL catalog (names + declared types),
    // so it can only pick capabilities that exist. Strongly biased to "single" to avoid over-decomposing.
    private async Task<DecompositionPlan> DecomposeAsync(string question, IReadOnlyList<Capability> catalog, CancellationToken ct)
    {
        string catalogText = catalog.Count == 0
            ? "(none)"
            : string.Join("\n", catalog.Select(c => $"- {c.Name} [{CapTypes.Name(c.InputType)}→{CapTypes.Name(c.OutputType)}]: {c.Description}"));

        const string sys = """
            You plan how to answer a question using ONLY the listed capabilities. Choose one mode:
              "single" — one capability (or none of these) handles it. This is the common case.
              "chain"  — it genuinely needs SEVERAL capabilities run in order, where one capability's
                         OUTPUT becomes the next capability's INPUT. Return the ordered capability
                         names, chosen EXACTLY from the list.
              "none"   — it needs a capability that is not in the list.
            Bias strongly to "single": only choose "chain" when the question truly has multiple
            sequential steps that feed each other. Never invent capability names.
            Output ONLY JSON (no prose, no fences), one of:
              {"mode":"single"}
              {"mode":"chain","steps":["name1","name2"]}
              {"mode":"none","reason":"<short reason>"}
            """;
        try
        {
            string raw = await _client!.CompleteAsync(sys, $"Capabilities:\n{catalogText}\n\nQuestion: \"{question}\"\nJSON:", ct);
            using JsonDocument doc = JsonDocument.Parse(StripFences(raw));
            JsonElement root = doc.RootElement;
            string mode = root.TryGetProperty("mode", out var m) ? m.GetString() ?? "single" : "single";
            var steps = new List<string>();
            if (root.TryGetProperty("steps", out var s) && s.ValueKind == JsonValueKind.Array)
                foreach (JsonElement e in s.EnumerateArray())
                { string? n = e.GetString(); if (!string.IsNullOrWhiteSpace(n)) steps.Add(n.Trim()); }
            string reason = root.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            return new DecompositionPlan(mode, steps, reason);
        }
        catch { return new DecompositionPlan("single", new List<string>(), ""); }
    }

    // Run one chain step, distinguishing a real failure (exception/timeout) from a normal answer
    // so a mid-chain failure stops the whole composition instead of being passed on as input.
    private static async Task<(bool Ok, string Output)> RunStepAsync(IHandler handler, string input)
    {
        try { string r = await Task.Run(() => handler.Handle(input)).WaitAsync(TimeSpan.FromSeconds(30)); return (true, r); }
        catch (TimeoutException) { return (false, "the capability took too long to run"); }
        catch (Exception ex) { return (false, ex.GetBaseException().Message); }
    }

    private static string Trunc(string s) => s.Length <= 80 ? s : s[..77] + "...";

    private static string StripFences(string s)
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

/// <summary>Which of the three outcomes the answer path produced.</summary>
public enum AnswerKind
{
    Declined,          // not a task — Text is a conversational reply, no capability touched
    Answered,          // a capability ran — Text is its output, Capability is its name
    GenerationFailed,  // a task, but no working handler could be built — Text is a short reason
}

/// <summary>
/// The result of <see cref="AgentCore.AnswerAsync"/>. Carries enough for each caller to keep its
/// own behavior: the two-node REPL skips/declines/answers differently from the swarm, but both
/// read the same structured outcome.
/// </summary>
public sealed record AnswerResult(AnswerKind Kind, string Text, string? Capability)
{
    public static AnswerResult Declined(string reply) => new(AnswerKind.Declined, reply, null);
    public static AnswerResult Answered(string text, string capability) => new(AnswerKind.Answered, text, capability);
    public static AnswerResult GenerationFailed(string text) => new(AnswerKind.GenerationFailed, text, null);
}

// ── rung 5a deliberation types (shared by AgentCore + SwarmAgent, sent over the wire as JSON) ──

/// <summary>The coordinator's one-call deliberation setup: the declared input/output types every
/// candidate must target, plus the test cases (consistent with those types) to score them on.</summary>
public sealed record DeliberationSpec(CapType InputType, CapType OutputType, IReadOnlyList<TestCase> Tests);

/// <summary>A test for a capability: run the tool on <see cref="Input"/>; the correct answer must
/// contain <see cref="Expected"/> (case-insensitive substring). Generated by the coordinator.</summary>
public sealed record TestCase(string Input, string Expected);

/// <summary>The outcome of running one candidate handler against one <see cref="TestCase"/>.</summary>
public sealed record TestResult(string Input, string Expected, string Got, bool Pass);

/// <summary>How a node's candidate turned out.</summary>
public enum CandidateStatus { Ok, Declined, GenerationFailed }

/// <summary>One node's competing candidate: its answer, the capability it used/built (name +
/// description), the SOURCE it generated (empty for an existing handler — only the winner's source
/// is ever pushed, in 5b), and how it did on the test cases.</summary>
public sealed record Candidate(
    CandidateStatus Status, string Answer, string? Capability, string? Description, string Source,
    IReadOnlyList<TestResult> TestResults);

// ── composition types ──

/// <summary>The decomposition step's verdict: "single" / "chain" (ordered capability names) / "none".</summary>
internal sealed record DecompositionPlan(string Mode, IReadOnlyList<string> Steps, string Reason);

/// <summary>How a <see cref="AgentCore.ComposeAsync"/> turned out.</summary>
public enum CompositionKind { Chain, NotComposite, Failed }

/// <summary>The outcome of a composition attempt: a chained answer (+ the steps used), a single-
/// capability answer (it wasn't composite), or a clean failure with the reason.</summary>
public sealed record CompositionResult(CompositionKind Kind, string Text, IReadOnlyList<string> Steps)
{
    public static CompositionResult Chain(string text, IReadOnlyList<string> steps) => new(CompositionKind.Chain, text, steps);
    public static CompositionResult NotComposite(string text) => new(CompositionKind.NotComposite, text, Array.Empty<string>());
    public static CompositionResult Failed(string text) => new(CompositionKind.Failed, text, Array.Empty<string>());
}
