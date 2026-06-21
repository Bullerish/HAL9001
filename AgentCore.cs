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
    private readonly TursoClient? _turso;   // the hive's shared knowledge store (facts), or null if no creds

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
        _turso = TursoClient.FromEnvironment();
        if (client is not null)
        {
            _generator = new HandlerGenerator(client, Registry, Git);
            _router = new CapabilityRouter(client, Registry);
        }
    }

    /// <summary>True when the hive's shared knowledge store (Turso) is configured.</summary>
    public bool HasHive => _turso is not null;

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

    // ── stored knowledge: explicit typed FACTS in the shared Turso hive ──────────────────
    //
    // A FACT is a noun — a piece of knowledge the hive holds (capital-of-ohio → Columbus, String) —
    // distinct from a HANDLER (a verb that computes). Facts live in one shared Turso table, so a fact
    // stored by any node is known to all and persists across restarts. This bite is EXPLICIT storage
    // + retrieval + routing only: no auto-deriving facts from handler runs, no updating/staleness, no
    // inference over facts.

    /// <summary>Bootstrap the shared facts table (any node can call it — CREATE TABLE IF NOT EXISTS).</summary>
    public async Task EnsureHiveAsync()
    {
        if (_turso is null) return;
        await _turso.ExecuteAsync(
            "CREATE TABLE IF NOT EXISTS facts (key TEXT PRIMARY KEY, value TEXT NOT NULL, type TEXT NOT NULL, updated_at TEXT NOT NULL)");
    }

    /// <summary>
    /// EXPLICIT storage: parse a "remember ..." statement into a fact and write it to the hive.
    /// KEY + VALUE come from a small LLM parse (key = a short kebab-case identifier of what the fact is
    /// about, value = the knowledge); TYPE is inferred from the value via <see cref="CapTypes.InferFromValue"/>
    /// ("Columbus" → String, "42" → Int). INSERT OR REPLACE so a re-stored key is overwritten (this is
    /// explicit storage, not staleness handling). Returns the stored fact, or null if it couldn't parse.
    /// </summary>
    public async Task<Fact?> RememberFactAsync(string statement, CancellationToken ct = default)
    {
        if (_turso is null || _client is null) return null;
        const string sys = """
            Extract ONE stored fact from the user's "remember" statement. Output ONLY JSON:
              {"key":"<short kebab-case identifier of what the fact is about>","value":"<the fact's value>"}
            e.g. "the capital of Ohio is Columbus" -> {"key":"capital-of-ohio","value":"Columbus"}.
            The value is the bare knowledge (a name, number, yes/no, or date) — no extra words.
            """;
        string key, value;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(StripFences(await _client.CompleteAsync(sys, $"Statement: \"{statement}\"\nJSON:", ct)));
            key = (doc.RootElement.TryGetProperty("key", out var k) ? k.GetString() : null)?.Trim() ?? "";
            value = (doc.RootElement.TryGetProperty("value", out var v) ? v.GetString() : null)?.Trim() ?? "";
        }
        catch { return null; }
        if (key.Length == 0 || value.Length == 0) return null;

        CapType type = CapTypes.InferFromValue(value);
        await _turso.ExecuteAsync(
            "INSERT OR REPLACE INTO facts (key, value, type, updated_at) VALUES (?, ?, ?, ?)",
            key, value, CapTypes.Name(type), DateTime.UtcNow.ToString("o"));
        return new Fact(key, value, type);
    }

    /// <summary>
    /// KNOWLEDGE-LOOKUP: does a stored fact directly answer this question? Lists the hive's fact keys,
    /// and (only if any exist) asks the LLM to CONSERVATIVELY pick the one fact that IS the answer, or
    /// none. On a match, returns the typed fact (caller returns it WITHOUT running or generating a
    /// handler). Returns null — fall through to the normal handler/generate/compose flow — when there's
    /// no hive, no facts, no key, or no real match. Deliberately conservative so it never steals a
    /// question that should run a handler.
    /// </summary>
    public async Task<Fact?> TryAnswerFromKnowledgeAsync(string question, CancellationToken ct = default)
    {
        if (_turso is null || _client is null) return null;

        List<List<string?>> rows;
        try { rows = await _turso.ExecuteAsync("SELECT key, value, type FROM facts"); }
        catch { return null; } // hive unreachable → behave as if no fact (fall through)
        if (rows.Count == 0) return null;

        var facts = rows.Where(r => r.Count >= 3 && r[0] is not null)
                        .Select(r => new Fact(r[0]!, r[1] ?? "", CapTypes.Parse(r[2])))
                        .ToList();
        if (facts.Count == 0) return null;

        string list = string.Join("\n", facts.Select(f => $"- {f.Key}: {f.Value}"));
        const string sys = """
            You decide whether a stored FACT directly answers a question. You are given the question and
            a list of known facts ("key: value"). If EXACTLY ONE fact is itself the answer, return its
            key. If none directly answers it, return "none". Be conservative: only match when the fact IS
            the answer — never match a vaguely related fact, and never match a question that asks to
            compute/transform something. Output ONLY JSON: {"key":"<fact-key-or-none>"}
            """;
        string picked;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(StripFences(await _client.CompleteAsync(sys, $"Facts:\n{list}\n\nQuestion: \"{question}\"\nJSON:", ct)));
            picked = (doc.RootElement.TryGetProperty("key", out var k) ? k.GetString() : null)?.Trim() ?? "none";
        }
        catch { return null; }

        return facts.FirstOrDefault(f => string.Equals(f.Key, picked, StringComparison.OrdinalIgnoreCase));
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
            Then write 2-3 SMALL test cases consistent with those types. If the output type is Bool,
            each test's "expected" MUST be exactly "yes" or "no".
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

        // RESOLVE each step against the registry; note which are missing.
        var resolved = new Capability?[plan.Steps.Count];
        var missing = new List<int>();
        for (int i = 0; i < plan.Steps.Count; i++)
        {
            if (Registry.TryGetCapability(plan.Steps[i], out Capability c)) resolved[i] = c;
            else missing.Add(i);
            // >>> nested composition (a step that is itself a chain) would attach HERE (later rung). <<<
        }

        // MISSING-LINK-COUNT GATE: 0 → run as before; 1 → single-link generation; EXACTLY 2 →
        // multi-link generation (below); 3+ → clean failure, generate NOTHING. Cap is hard at 2.
        int N = plan.Steps.Count;
        if (missing.Count > 2)
            return CompositionResult.Failed(
                $"cannot compose: {missing.Count} capabilities missing " +
                $"({string.Join(", ", missing.Select(i => $"'{plan.Steps[i]}'"))}) — at most 2 can be generated");

        // Derive the type flowing at chain boundary b (0..N): boundary b is the seam between step b-1
        // and step b. A PRESENT capability on either side pins it authoritatively (its real type); an
        // end with a present step uses that; otherwise (both sides missing/absent — the adjacent-
        // missing-link case, or an end whose step is missing) it comes from decomposition's declared
        // seam-type vector, falling back to the chain's overall input/output type. Because each
        // boundary has ONE value, two ADJACENT missing links read the SAME type at their shared seam,
        // so their invented types are consistent by construction.
        CapType Boundary(int b)
        {
            if (b > 0 && resolved[b - 1] is Capability lp) return lp.OutputType;          // present left → its output
            if (b < N && resolved[b] is Capability rp) return rp.InputType;               // present right → its input
            if (plan.SeamTypes.Count == N + 1) return plan.SeamTypes[b];                   // decompose's declared boundary type
            return b == 0 ? plan.InputType : (b == N ? plan.OutputType : CapType.String);  // overall ends / safe default
        }

        // DISPLAY the plan BEFORE generating/executing — marking each link to be generated, with the
        // seam-derived types. Adjacent missing links share Boundary(k+1), so their seam types agree.
        string planLine = "[composition] plan: " + string.Join(" → ", plan.Steps.Select((name, i) =>
            resolved[i] is Capability rc
                ? $"{name} [{CapTypes.Name(rc.InputType)}→{CapTypes.Name(rc.OutputType)}]"
                : $"{name} [{CapTypes.Name(Boundary(i))}→{CapTypes.Name(Boundary(i + 1))}] (MISSING — will generate)"));
        Console.WriteLine(planLine);

        // GENERATE + VALIDATE every missing link (chain order), each held LOCAL (persist:false,
        // registered, NOT pushed). ALL-OR-NOTHING: only if EVERY missing link clears the 5b floor do
        // we adopt them; if any fails, remove every link generated so far from the registry and push
        // NOTHING — a failed multi-link composition leaves the shared catalog completely unchanged.
        var generated = new List<(int Index, string Name, CapType In, CapType Out, GeneratedLink Link)>();
        foreach (int k in missing) // ascending = chain order
        {
            CapType linkIn = Boundary(k), linkOut = Boundary(k + 1);
            GeneratedLink? link = await GenerateValidatedLinkAsync(question, plan.Steps[k], linkIn, linkOut, ct);
            if (link is null)
            {
                foreach (var g in generated) Registry.Remove(g.Name); // un-register the validated-but-unused siblings → no trace
                return CompositionResult.Failed($"cannot compose: couldn't generate a working '{plan.Steps[k]}' that passes validation (nothing adopted)");
            }
            generated.Add((k, plan.Steps[k], linkIn, linkOut, link));
        }

        // Every missing link validated → ADOPT them all now (push each exactly once, with its types).
        foreach (var g in generated)
        {
            Console.WriteLine($"[composition] adopting '{g.Name}' [{CapTypes.Name(g.In)}→{CapTypes.Name(g.Out)}] ({g.Link.Passed}/{g.Link.Total} tests) — push to swarm.");
            TryPersistWinner(g.Name, g.Link.Description, g.Link.Example, g.Link.Source, g.In, g.Out);
            Registry.TryGetCapability(g.Name, out Capability nc); // registered by generation
            resolved[g.Index] = nc;
        }

        var caps = resolved.Select(c => c!).ToList();

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
            You plan how to answer a question. Decide the MODE from the QUESTION'S STRUCTURE — how many
            sequential sub-tasks it has — NOT from what already exists:
              "single" — the question is ONE operation (a single lookup, computation, or transformation).
              "chain"  — the question has SEVERAL sequential sub-tasks where one sub-task's RESULT feeds
                         the next (e.g. "convert X to Y, then check the result"). List the ordered steps.
                         For each step, use the EXACT name of an existing capability if one fits; if a
                         step has NO matching capability in the list, give it a clear kebab-case name —
                         it will be generated. Also give the chain's overall input and output types, and
                         a "types" array: the data type flowing at EACH boundary of the chain (length =
                         number of steps + 1 — the input to step 1, the type between each pair of steps,
                         and the final output). All types are from: String, Int, Number, Bool, Date.
              "none"   — it cannot be answered by computation at all.
            Choose "chain" whenever the question genuinely has multiple sequential steps that feed each
            other — EVEN IF a needed capability is not in the list (name it; it will be built). Use
            "single" only for a truly one-step question; never pad a simple question with extra steps.
            Output ONLY JSON (no prose, no fences), one of:
              {"mode":"single"}
              {"mode":"chain","steps":["name1","name2"],"inputType":"<type>","outputType":"<type>","types":["<type>","<type>","<type>"]}
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
            CapType inT = CapTypes.Parse(root.TryGetProperty("inputType", out var it) ? it.GetString() : null);
            CapType outT = CapTypes.Parse(root.TryGetProperty("outputType", out var ot) ? ot.GetString() : null);
            List<CapType>? seams = null;
            if (root.TryGetProperty("types", out var ts) && ts.ValueKind == JsonValueKind.Array)
            {
                seams = new List<CapType>();
                foreach (JsonElement e in ts.EnumerateArray()) seams.Add(CapTypes.Parse(e.GetString()));
                // Only trust it if it has one type per boundary (steps + 1); otherwise ignore it.
                if (seams.Count != steps.Count + 1) seams = null;
            }
            return new DecompositionPlan(mode, steps, reason, inT, outT, seams);
        }
        catch { return new DecompositionPlan("single", new List<string>(), ""); }
    }

    /// <summary>The 5b quality floor, shared by competitive deliberation and missing-link generation:
    /// a candidate must pass a MAJORITY of its test cases to be adopted.</summary>
    public static bool ClearsQualityFloor(int passed, int total) => total > 0 && passed * 2 > total;

    // Generate the ONE missing chain link, type-constrained by the seam, and VALIDATE it against
    // generated test cases to the same 5b quality floor before it may be used/adopted. Registers the
    // link locally on success (caller pushes it); removes a failed link so it's not later mistaken
    // for an existing capability. One capped retry, same spirit as normal generation.
    private async Task<GeneratedLink?> GenerateValidatedLinkAsync(
        string question, string name, CapType inputType, CapType outputType, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            (string desc, string example, IReadOnlyList<TestCase> tests) = await PrepareLinkAsync(question, name, inputType, outputType, ct);
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                GeneratedHandler? gen;
                try { gen = await _generator!.GenerateAsync(name, desc, example, ct, persist: false, inputType, outputType); }
                catch (Exception ex) { Console.WriteLine($"  [composition] link generation error: {ex.Message}"); gen = null; }
                if (gen is null) continue;

                int passed = 0, total = tests.Count;
                foreach (TestCase tc in tests)
                {
                    string got = CapTypes.Matches(inputType, tc.Input) ? await RunHandlerAsync(gen.Handler, tc.Input) : "";
                    if (got.Contains(tc.Expected, StringComparison.OrdinalIgnoreCase)) passed++;
                }
                Console.WriteLine($"  [composition] link '{name}' attempt {attempt}: validation {passed}/{total}");
                if (ClearsQualityFloor(passed, total))
                    return new GeneratedLink(gen.Source, desc, example, passed, total);
            }
            Registry.Remove(name); // don't leave a failed/unvalidated link registered as if it existed
            return null;
        }
        finally { _gate.Release(); }
    }

    // Prepare a missing link for generation: given the overall question and the link's FIXED name +
    // input/output types, ask for a one-line description, an example request, and type-consistent
    // test cases. Best-effort with sensible fallbacks.
    private async Task<(string Description, string Example, IReadOnlyList<TestCase> Tests)> PrepareLinkAsync(
        string question, string name, CapType inputType, CapType outputType, CancellationToken ct)
    {
        string fallbackDesc = $"a '{name}' capability that takes {CapTypes.Name(inputType)} and returns {CapTypes.Name(outputType)}";
        if (_client is null) return (fallbackDesc, question, Array.Empty<TestCase>());

        const string sys = """
            A multi-step question is being answered by chaining tools, and ONE tool in the chain must
            be built. Given the overall question and that tool's NAME and FIXED input/output types,
            produce: a one-line description of what THAT tool does, one example request for it, and
            2-3 test cases (input -> the key substring the correct answer must contain) consistent
            with its types. If the output type is Bool, each test's "expected" MUST be exactly "yes"
            or "no". Output ONLY JSON (no prose/fences):
              {"description":"...","example":"...","tests":[{"input":"...","expected":"..."}]}
            """;
        string user = $"Overall question: \"{question}\"\nTool to build: \"{name}\"\n" +
                      $"Input type: {CapTypes.Name(inputType)}\nOutput type: {CapTypes.Name(outputType)}\nJSON:";
        try
        {
            string raw = await _client.CompleteAsync(sys, user, ct);
            using JsonDocument doc = JsonDocument.Parse(StripFences(raw));
            JsonElement root = doc.RootElement;
            string desc = root.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            string example = root.TryGetProperty("example", out var e) ? e.GetString() ?? "" : "";
            var tests = new List<TestCase>();
            if (root.TryGetProperty("tests", out var t) && t.ValueKind == JsonValueKind.Array)
                foreach (JsonElement el in t.EnumerateArray())
                {
                    string ip = el.TryGetProperty("input", out var ii) ? ii.GetString() ?? "" : "";
                    string ex = el.TryGetProperty("expected", out var xx) ? xx.GetString() ?? "" : "";
                    if (ip.Length > 0 && ex.Length > 0) tests.Add(new TestCase(ip, ex));
                    if (tests.Count == 4) break;
                }
            return (desc.Length > 0 ? desc : fallbackDesc, example.Length > 0 ? example : question, tests);
        }
        catch { return (fallbackDesc, question, Array.Empty<TestCase>()); }
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

/// <summary>A stored piece of hive knowledge: an identifier <see cref="Key"/>, its stored
/// <see cref="Value"/>, and the declared <see cref="Type"/> (so a fact can later feed a typed
/// handler input — not built this bite). The hive's "noun", distinct from a handler's "verb".</summary>
public sealed record Fact(string Key, string Value, CapType Type);

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

/// <summary>The decomposition step's verdict: "single" / "chain" (ordered capability names; a step
/// may name a not-yet-existing capability to be generated) / "none". For a chain it also carries the
/// chain's overall input/output types (used to pin a first/last missing link's outer type).</summary>
internal sealed record DecompositionPlan(
    string Mode, IReadOnlyList<string> Steps, string Reason,
    CapType InputType = CapType.String, CapType OutputType = CapType.String,
    IReadOnlyList<CapType>? SeamTypesOrNull = null)
{
    // The data type at each chain boundary (length = Steps+1) when the model supplied a consistent
    // one; used to pin a seam between two ADJACENT missing links. Empty if unavailable.
    public IReadOnlyList<CapType> SeamTypes => SeamTypesOrNull ?? Array.Empty<CapType>();
}

/// <summary>A generated-and-validated missing chain link: its source (to push), description +
/// example (for the header/adoption), and its validation score.</summary>
internal sealed record GeneratedLink(string Source, string Description, string Example, int Passed, int Total);

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
