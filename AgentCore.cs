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
    private readonly IdentityStore _identityStore; // the hive's persistent self (name/born/concept/persona)
    private readonly SelfModel _selfModel;  // answers "what am I?" from real state (registry/facts/events)

    /// <summary>The hive's persistent identity (name, birth, self-concept, persona), loaded/born at
    /// <see cref="EnsureHiveAsync"/>. Null until then, or when there's no hive.</summary>
    public HiveIdentity? Identity { get; private set; }

    // One gate for the whole answer path. The registry and generator are shared mutable state;
    // serializing answer production keeps two concurrent callers from interleaving a half-built
    // handler. (This replaces AgentRepl's `requestGate` and SwarmAgent's `answerGate`.)
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The capabilities this node knows. Shared by callers for catalog/count/lookup.</summary>
    public HandlerRegistry Registry { get; } = new();

    /// <summary>The git repo handlers are synced through, or null if we're not inside one.</summary>
    public GitSync? Git { get; }

    /// <summary>The hive's autobiographical event log (episodic memory). Shares the same Turso store
    /// as facts; significant acts on this answer path append to it. The host sets <c>Events.Actor</c>
    /// (the swarm to its node id; the lone agent leaves the default "single").</summary>
    public EventLog Events { get; }

    /// <summary>True when an API key was available — i.e. we can actually route + generate.</summary>
    public bool HasLlm => _router is not null && _generator is not null;

    public AgentCore(AnthropicClient? client)
    {
        _client = client;
        Git = GitSync.Discover();
        _turso = TursoClient.FromEnvironment();
        Events = new EventLog(_turso, "single"); // shares the hive store; actor overridden by the swarm
        _identityStore = new IdentityStore(_turso, client); // the hive's persistent, self-chosen identity
        // Grounded introspection over real state — now speaking as the hive's persisted identity, in
        // its persona's voice (the voice pass uses the LLM; facts always come from state).
        _selfModel = new SelfModel(Registry, _turso, Events, () => Identity, client);
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
            "CREATE TABLE IF NOT EXISTS facts (key TEXT PRIMARY KEY, value TEXT NOT NULL, type TEXT NOT NULL, source TEXT NOT NULL DEFAULT 'explicit', updated_at TEXT NOT NULL)");
        // Migrate a pre-provenance facts table (created before this bite) by adding the source column.
        // ALTER throws "duplicate column" when the column already exists (fresh table) — ignore that.
        try { await _turso.ExecuteAsync("ALTER TABLE facts ADD COLUMN source TEXT NOT NULL DEFAULT 'explicit'"); }
        catch { /* column already present */ }
        // Bootstrap episodic memory alongside knowledge — the events table lives in the same hive.
        await Events.EnsureAsync();
        // Bootstrap + BIRTH the hive's persistent identity (once, atomically). After this, every node
        // and every restart shares the same self. (Birth records the right actor because the swarm
        // sets Events.Actor before calling EnsureHiveAsync.)
        await _identityStore.EnsureTableAsync();
        Identity = await _identityStore.EnsureBornAsync(Events);
        // Self-critique (bite 5): per-capability self-assessments (latest confidence score).
        await _turso.ExecuteAsync(
            "CREATE TABLE IF NOT EXISTS assessments (capability TEXT PRIMARY KEY, confidence REAL NOT NULL, " +
            "passed INT NOT NULL, total INT NOT NULL, notes TEXT, assessed_at TEXT NOT NULL, assessed_by TEXT NOT NULL)");
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
            "INSERT OR REPLACE INTO facts (key, value, type, source, updated_at) VALUES (?, ?, ?, 'explicit', ?)",
            key, value, CapTypes.Name(type), DateTime.UtcNow.ToString("o"));
        await Events.AppendAsync("fact-remembered",
            $"remembered '{key}' = {value} ({CapTypes.Name(type)})", key);
        return new Fact(key, value, type, "explicit");
    }

    /// <summary>
    /// AUTO-DERIVE a cached fact from a STABLE capability's answer (the auto-derivation gate is the
    /// caller — only Stable answers reach here). The fact is keyed by a slug of the question, typed by
    /// the capability's output type, and marked source='derived' so it's distinct from explicit facts
    /// (and purgeable later without touching them). LIVE answers are never derived, which is what makes
    /// caching safe. No-op without a hive.
    /// </summary>
    private async Task DeriveFactAsync(string question, string value, CapType type, string fromCapability)
    {
        if (_turso is null) return;
        string key = Slug(question);
        if (key.Length == 0) return;
        try
        {
            await _turso.ExecuteAsync(
                "INSERT OR REPLACE INTO facts (key, value, type, source, updated_at) VALUES (?, ?, ?, 'derived', ?)",
                key, value, CapTypes.Name(type), DateTime.UtcNow.ToString("o"));
            Console.WriteLine($"  [knowledge] derived fact '{key}' = {value} ({CapTypes.Name(type)}) from stable '{fromCapability}' — cached");
            await Events.AppendAsync("fact-derived",
                $"derived '{key}' = {value} ({CapTypes.Name(type)}) from stable '{fromCapability}'", key);
        }
        catch (Exception ex) { Console.WriteLine($"  [knowledge] could not derive fact: {ex.Message}"); }
    }

    // A stable, readable key from a question, for derived facts: lowercase, alphanumerics → dashes.
    private static string Slug(string question)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in question.Trim().ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(c) ? c : '-');
        string s = string.Join("-", sb.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
        return s.Length <= 80 ? s : s[..80];
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
        try { rows = await _turso.ExecuteAsync("SELECT key, value, type, source FROM facts"); }
        catch { return null; } // hive unreachable → behave as if no fact (fall through)
        if (rows.Count == 0) return null;

        var facts = rows.Where(r => r.Count >= 4 && r[0] is not null)
                        .Select(r => new Fact(r[0]!, r[1] ?? "", CapTypes.Parse(r[2]), r[3] ?? "explicit"))
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

            // NOT A TASK → conversational reply; touch nothing in the compile/push pipeline. If the
            // declined input still LOOKS like a real query (not a one-word greeting), record it as a
            // GAP — curiosity may later reconsider whether it was actually a buildable task.
            if (decision.Action == RouteAction.Decline)
            {
                if (LooksLikeQuery(request))
                    await Events.AppendAsync("gap-noticed", $"declined \"{request}\" — may be a task I could learn", request);
                return AnswerResult.Declined(decision.Reply);
            }

            // ABOUT ITSELF → answer from the SELF-MODEL: real state (registry + facts + episodic
            // memory), rendered by code. The LLM only recognized the question as introspective and
            // picked the topic — it never fabricates a capability or a fact it doesn't have.
            if (decision.Action == RouteAction.Introspect)
                return AnswerResult.Answered(
                    decision.SelfTopic == SelfTopic.Mood
                        ? await DescribeMoodAsync()                              // "how are you?" → real drives
                        : await _selfModel.DescribeAsync(decision.SelfTopic),    // other self-questions
                    decision.SelfTopic == SelfTopic.Mood ? "mood" : "self-model");

            IHandler? handler;
            string usedName;
            CapType inType;            // declared input type of the chosen capability — for the boundary check
            CapType outType;           // declared output type — used to type an auto-derived fact
            StabilityKind stability;   // Stable → may auto-derive a cached fact; Live → recompute, never cache
            if (decision.Action == RouteAction.UseExisting && Registry.TryGetCapability(decision.Name, out Capability cap))
            {
                handler = cap.Handler;
                usedName = decision.Name;
                inType = cap.InputType; outType = cap.OutputType; stability = cap.Stability;
                Console.WriteLine($"  (using capability '{usedName}' [{CapTypes.Name(cap.InputType)}→{CapTypes.Name(cap.OutputType)}, {StabilityKinds.Name(cap.Stability)}])");
            }
            else
            {
                // CreateNew — or a UseExisting that named something we don't actually have
                // (an LLM slip): commission a general capability either way, with declared types + stability.
                string capName = decision.Name.Length > 0 ? decision.Name : "capability";
                string capDesc = decision.Description.Length > 0 ? decision.Description : request;
                usedName = capName;
                inType = decision.InputType; outType = decision.OutputType; stability = decision.Stability;
                Console.WriteLine($"  (commissioning '{capName}' [{CapTypes.Name(decision.InputType)}→{CapTypes.Name(decision.OutputType)}, {StabilityKinds.Name(decision.Stability)}]: {capDesc})");
                GeneratedHandler? gen;
                try
                {
                    gen = await _generator!.GenerateAsync(capName, capDesc, request, ct, persist: true, decision.InputType, decision.OutputType, decision.Stability);
                }
                catch (Exception ex)
                {
                    // GAP: recognized a task but couldn't build the tool — record it for curiosity.
                    await Events.AppendAsync("gap-noticed", $"couldn't build a capability for \"{request}\" ({ex.Message})", request);
                    return AnswerResult.GenerationFailed($"(generation failed: {ex.Message})");
                }
                if (gen is null)
                {
                    await Events.AppendAsync("gap-noticed", $"couldn't build a working capability for \"{request}\"", request);
                    return AnswerResult.GenerationFailed("(couldn't build a working handler)");
                }
                handler = gen.Handler;
                // EPISODIC MEMORY: commissioning a new capability is a significant act — the hive
                // learned a new skill. Record it (best-effort; never blocks the answer).
                await Events.AppendAsync("capability-commissioned",
                    $"commissioned '{capName}' [{CapTypes.Name(inType)}→{CapTypes.Name(outType)}, {StabilityKinds.Name(stability)}]: {capDesc}",
                    capName);
            }

            // Boundary parse-check (typed-capabilities rung): if the input can't possibly hold the
            // declared input type, return a clean typed error instead of running the handler on garbage.
            if (!CapTypes.Matches(inType, request))
                return AnswerResult.Answered(CapTypes.Mismatch(inType, request), usedName);

            // Run the compiled capability with a timeout so a hung network call in generated
            // code can't freeze the agent, and catch any runtime throw — never crash. A LIVE
            // capability reads the REAL clock here (Clock.Injected is null in production).
            try
            {
                string result = await Task.Run(() => handler!.Handle(request), ct)
                                          .WaitAsync(TimeSpan.FromSeconds(30), ct);

                if (stability == StabilityKind.Live)
                    // Live: recompute every call, NEVER cache a value (it'd be stale tomorrow).
                    Console.WriteLine($"  [live] recomputed '{usedName}' against the real clock ({Clock.Today:yyyy-MM-dd}) — not cached");
                else
                    // Stable (pure): auto-derive a cached fact so a repeat of this exact question is
                    // served from the hive without recomputing. Gated on Stable — this is why
                    // auto-derivation can't go stale.
                    await DeriveFactAsync(request, result, outType, usedName);

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

    // ── CURIOSITY (sentience ladder, bite 4): notice gaps, propose + commission to fill them ──
    //
    // The hive's first real INITIATIVE. Failures it has lived through — questions it declined, tasks
    // it couldn't build, compositions it couldn't complete — are recorded in the episodic log as
    // "gap-noticed" events (see AnswerAsync / ComposeAsync). When idle, the hive mines those gaps,
    // and for each genuine-but-unmet task it PROPOSES a capability to fill it. Nothing is built
    // without approval (the propose→approve gate); on approval it commissions the tool and logs a
    // "curiosity-resolved" event ("I couldn't X, so I learned it"). It notices its own ignorance and
    // acts to fix it — but only with a human's yes, for now.

    // Gaps already proposed this session, so an idle re-scan doesn't keep re-proposing the same ones.
    private readonly HashSet<string> _proposedGaps = new(StringComparer.OrdinalIgnoreCase);

    // A declined input is worth reconsidering only if it still reads like a real query — not a
    // one-word greeting ("hi", "thanks"). Cheap heuristic; curiosity's LLM review is the real filter.
    private static bool LooksLikeQuery(string s)
        => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length >= 4 || s.Any(char.IsDigit);

    /// <summary>
    /// Mine the episodic log for unmet gaps and, for each genuine task a tool could answer, return a
    /// capability PROPOSAL. Skips gaps already resolved (a prior curiosity-resolved) or already
    /// proposed this session. Pure proposal — nothing is built here (that's the approve gate). Empty
    /// without an LLM.
    /// </summary>
    public async Task<IReadOnlyList<CuriosityProposal>> ReviewGapsAsync(int maxProposals = 3, int scan = 80)
    {
        if (_client is null) return Array.Empty<CuriosityProposal>();
        IReadOnlyList<HiveEvent> recent = await Events.RecentAsync(scan); // oldest→newest

        // Requests already satisfied by curiosity before — never re-learn them.
        var resolved = new HashSet<string>(
            recent.Where(e => e.Kind == "curiosity-resolved" && !string.IsNullOrEmpty(e.Ref)).Select(e => e.Ref!),
            StringComparer.OrdinalIgnoreCase);

        // Candidate gaps, newest first, de-duplicated, minus resolved/already-proposed.
        var gaps = recent.Where(e => e.Kind == "gap-noticed" && !string.IsNullOrEmpty(e.Ref))
                         .Select(e => e.Ref!)
                         .Reverse()
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .Where(r => !resolved.Contains(r) && !_proposedGaps.Contains(r))
                         .ToList();

        var proposals = new List<CuriosityProposal>();
        foreach (string req in gaps)
        {
            if (proposals.Count >= maxProposals) break;
            _proposedGaps.Add(req); // don't reconsider this gap again this session, build-or-skip
            CuriosityProposal? p = await ProposeForGapAsync(req);
            if (p is not null) proposals.Add(p);
        }
        return proposals;
    }

    // Ask the LLM whether a single gap is a buildable task and, if so, propose a general capability.
    private async Task<CuriosityProposal?> ProposeForGapAsync(string request)
    {
        const string sys = """
            An agent DECLINED or FAILED the request below and recorded it as a GAP. Even if it wasn't
            phrased as a direct command, decide whether it gestures at a COMPUTABLE DOMAIN a
            self-contained tool could serve — a computation, a conversion, a lookup over baked data, or
            a fact about numbers/words/dates (e.g. "roman numerals are cool" → a number→roman-numeral
            converter; "the fibonacci sequence is neat" → an Nth-Fibonacci tool). If so, propose a
            GENERAL capability for that whole class. Skip ONLY pure chit-chat, greetings, opinions about
            the agent, feelings, or anything with no computable task at all. Output ONLY JSON:
              {"build":true,"name":"<short-kebab-id>","description":"<one line: the general capability>","inputType":"<String|Int|Number|Bool|Date>","outputType":"<String|Int|Number|Bool|Date>","stability":"<stable|live>"}
              {"build":false}
            """;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(StripFences(await _client!.CompleteAsync(sys, $"Request: \"{request}\"\nJSON:")));
            JsonElement root = doc.RootElement;
            if (!(root.TryGetProperty("build", out var b) && b.ValueKind == JsonValueKind.True)) return null;
            string name = (root.TryGetProperty("name", out var n) ? n.GetString() : null)?.Trim() ?? "";
            string desc = (root.TryGetProperty("description", out var d) ? d.GetString() : null)?.Trim() ?? "";
            if (name.Length == 0 || desc.Length == 0) return null;
            string inT = root.TryGetProperty("inputType", out var it) ? it.GetString() ?? "" : "";
            string outT = root.TryGetProperty("outputType", out var ot) ? ot.GetString() ?? "" : "";
            string stab = root.TryGetProperty("stability", out var st) ? st.GetString() ?? "" : "";
            return new CuriosityProposal(request, name, desc, CapTypes.Parse(inT), CapTypes.Parse(outT), StabilityKinds.Parse(stab));
        }
        catch { return null; }
    }

    /// <summary>
    /// APPROVE-AND-ACT: commission the capability a proposal describes (the same generate/compile/
    /// validate/push path as a normal commission), and on success log a "curiosity-resolved" event —
    /// "I couldn't X, so I learned it." Returns true if the capability was built.
    /// </summary>
    public async Task<bool> CommissionProposalAsync(CuriosityProposal p, CancellationToken ct = default)
    {
        if (_generator is null) return false;
        await _gate.WaitAsync(ct);
        try
        {
            GeneratedHandler? gen;
            try { gen = await _generator.GenerateAsync(p.Name, p.Description, p.Request, ct, persist: true, p.InputType, p.OutputType, p.Stability); }
            catch (Exception ex) { Console.WriteLine($"  [curiosity] couldn't learn '{p.Name}': {ex.Message}"); return false; }
            if (gen is null) { Console.WriteLine($"  [curiosity] couldn't build a working '{p.Name}'."); return false; }

            Console.WriteLine($"  [curiosity] learned '{p.Name}' [{CapTypes.Name(p.InputType)}→{CapTypes.Name(p.OutputType)}, {StabilityKinds.Name(p.Stability)}] to fill the gap: \"{p.Request}\"");
            await Events.AppendAsync("curiosity-resolved",
                $"I couldn't answer \"{p.Request}\", so I learned '{p.Name}' [{CapTypes.Name(p.InputType)}→{CapTypes.Name(p.OutputType)}, {StabilityKinds.Name(p.Stability)}] to do it",
                p.Request);
            return true;
        }
        finally { _gate.Release(); }
    }

    // ── SELF-CRITIQUE / REFLECTION (sentience ladder, bite 5): judge its own work, fix the weak ──
    //
    // Metacognition: the hive reasons about its OWN outputs. It SCORES a capability by generating
    // fresh test cases for it and RUNNING the compiled handler against them — confidence = pass rate,
    // grounded in real execution, not the model's opinion. Weak capabilities (below the same majority
    // quality floor used in deliberation) are flagged, and can be RE-WORKED: a fresh implementation is
    // generated and scored on the SAME tests, and adopted only if it MEASURABLY beats the current one.
    // Assessments persist (latest per capability) and each scoring/rework is an episodic event.

    private readonly HashSet<string> _assessedThisSession = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Generate a few fresh, typed test cases for a KNOWN capability (its types are already
    /// declared, so unlike deliberation we don't re-infer them). Empty without a key / on parse error.</summary>
    private async Task<IReadOnlyList<TestCase>> GenerateTestsForAsync(Capability cap, CancellationToken ct = default)
    {
        if (_client is null) return Array.Empty<TestCase>();
        string sys = $$"""
            Write 3 SMALL, INDEPENDENT test cases that check whether a tool works correctly. The tool's
            input type is {{CapTypes.Name(cap.InputType)}} and its output type is {{CapTypes.Name(cap.OutputType)}}.
            Each test is a concrete input request and the key substring the CORRECT output must contain.
            If the output type is Bool, "expected" MUST be exactly "yes" or "no". Choose inputs whose
            correct answers you are certain of. Output ONLY JSON (no prose/fences):
              {"tests":[{"input":"<a concrete request>","expected":"<key substring the right answer must contain>"}]}
            """;
        try
        {
            string raw = await _client.CompleteAsync(sys, $"Tool: {cap.Name} — {cap.Description}\nExample request: {cap.ExampleRequest}\nJSON:", ct);
            using JsonDocument doc = JsonDocument.Parse(StripFences(raw));
            var list = new List<TestCase>();
            if (doc.RootElement.TryGetProperty("tests", out JsonElement tests) && tests.ValueKind == JsonValueKind.Array)
                foreach (JsonElement e in tests.EnumerateArray())
                {
                    string input = e.TryGetProperty("input", out var ii) ? ii.GetString() ?? "" : "";
                    string expected = e.TryGetProperty("expected", out var xx) ? xx.GetString() ?? "" : "";
                    if (input.Length > 0 && expected.Length > 0) list.Add(new TestCase(input, expected));
                    if (list.Count == 4) break;
                }
            return list;
        }
        catch { return Array.Empty<TestCase>(); }
    }

    // Run a handler against a test set and return the pass rate (0..1). Pure measurement.
    private static async Task<(int Passed, int Total)> ScoreOnAsync(IHandler h, CapType inType, IReadOnlyList<TestCase> tests)
    {
        int passed = 0;
        foreach (TestCase tc in tests)
        {
            string got = CapTypes.Matches(inType, tc.Input) ? await RunHandlerAsync(h, tc.Input) : CapTypes.Mismatch(inType, tc.Input);
            if (got.Contains(tc.Expected, StringComparison.OrdinalIgnoreCase)) passed++;
        }
        return (passed, tests.Count);
    }

    /// <summary>
    /// SCORE one capability: generate fresh tests, run the live handler, confidence = pass rate.
    /// Persist the assessment (latest per capability) and log a self-critique event. Null if it can't
    /// be judged (no key / no tests generated).
    /// </summary>
    public async Task<SelfAssessment?> ScoreCapabilityAsync(Capability cap, CancellationToken ct = default)
    {
        IReadOnlyList<TestCase> tests = await GenerateTestsForAsync(cap, ct);
        if (tests.Count == 0) return null;

        int passed = 0; var fails = new List<string>();
        foreach (TestCase tc in tests)
        {
            string got = CapTypes.Matches(cap.InputType, tc.Input) ? await RunHandlerAsync(cap.Handler, tc.Input) : CapTypes.Mismatch(cap.InputType, tc.Input);
            if (got.Contains(tc.Expected, StringComparison.OrdinalIgnoreCase)) passed++;
            else fails.Add($"'{tc.Input}'→want '{tc.Expected}', got '{(got.Length > 40 ? got[..40] + "…" : got)}'");
        }
        double conf = (double)passed / tests.Count;
        string notes = fails.Count == 0 ? "passed all fresh tests" : "failing: " + string.Join("; ", fails);
        _assessedThisSession.Add(cap.Name);
        await SaveAssessmentAsync(cap.Name, conf, passed, tests.Count, notes);
        await Events.AppendAsync("self-critique",
            $"assessed '{cap.Name}': confidence {conf.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} ({passed}/{tests.Count}) — {(ClearsQualityFloor(passed, tests.Count) ? "looks solid" : "weak, worth re-working")}",
            cap.Name);
        return new SelfAssessment(cap.Name, conf, passed, tests.Count, notes);
    }

    private async Task SaveAssessmentAsync(string name, double conf, int passed, int total, string notes)
    {
        if (_turso is null) return;
        try
        {
            await _turso.ExecuteAsync(
                "INSERT OR REPLACE INTO assessments (capability, confidence, passed, total, notes, assessed_at, assessed_by) VALUES (?, ?, ?, ?, ?, ?, ?)",
                name, conf.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture), passed.ToString(), total.ToString(), notes,
                DateTime.UtcNow.ToString("o"), Events.Actor);
        }
        catch (Exception ex) { Console.WriteLine($"  [reflect] couldn't save assessment: {ex.Message}"); }
    }

    private async Task<HashSet<string>> LoadAssessedNamesAsync()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_turso is null) return set;
        try
        {
            List<List<string?>> rows = await _turso.ExecuteAsync("SELECT capability FROM assessments");
            foreach (var r in rows) if (r.Count > 0 && r[0] is not null) set.Add(r[0]!);
        }
        catch { /* unreachable → treat as none assessed */ }
        return set;
    }

    /// <summary>
    /// REFLECT: score up to <paramref name="maxToScore"/> capabilities that haven't been assessed yet
    /// (this session or ever), returning their assessments. Caller flags the weak ones (those that
    /// don't clear the quality floor). If everything's already assessed, re-scores some so reflection
    /// is never a silent no-op.
    /// </summary>
    public async Task<IReadOnlyList<SelfAssessment>> ReflectAsync(int maxToScore = 5, CancellationToken ct = default)
    {
        if (_client is null) return Array.Empty<SelfAssessment>();
        HashSet<string> assessed = await LoadAssessedNamesAsync();
        var caps = Registry.Catalog()
            .Where(c => !_assessedThisSession.Contains(c.Name) && !assessed.Contains(c.Name))
            .Take(maxToScore).ToList();
        if (caps.Count == 0) // all assessed before — re-examine a few not yet looked at THIS session
            caps = Registry.Catalog().Where(c => !_assessedThisSession.Contains(c.Name)).Take(maxToScore).ToList();

        var results = new List<SelfAssessment>();
        foreach (Capability c in caps)
        {
            SelfAssessment? a = await ScoreCapabilityAsync(c, ct);
            if (a is not null) results.Add(a);
        }
        return results;
    }

    /// <summary>Is a capability weak enough to flag for re-work? (Below the deliberation quality floor.)</summary>
    public static bool IsWeak(SelfAssessment a) => !ClearsQualityFloor(a.Passed, a.Total);

    /// <summary>
    /// RE-WORK a capability: generate fresh tests, score the CURRENT handler on them, generate a fresh
    /// implementation, score IT on the SAME tests, and adopt the new one only if it MEASURABLY beats
    /// the current (a real, comparable improvement — not just a different answer). Logs a self-improved
    /// event on adoption. Returns (improved, beforeConfidence, afterConfidence).
    /// </summary>
    public async Task<(bool Improved, double Before, double After)> ReworkAsync(string name, CancellationToken ct = default)
    {
        if (_generator is null || !Registry.TryGetCapability(name, out Capability cap)) return (false, 0, 0);
        await _gate.WaitAsync(ct);
        try
        {
            IReadOnlyList<TestCase> tests = await GenerateTestsForAsync(cap, ct);
            if (tests.Count == 0) { Console.WriteLine($"  [reflect] couldn't generate tests to re-work '{name}'."); return (false, 0, 0); }

            (int bp, int bt) = await ScoreOnAsync(cap.Handler, cap.InputType, tests);
            double before = (double)bp / bt;

            GeneratedHandler? gen;
            try { gen = await _generator.GenerateAsync(cap.Name, cap.Description, cap.ExampleRequest, ct, persist: false, cap.InputType, cap.OutputType, cap.Stability); }
            catch (Exception ex) { Console.WriteLine($"  [reflect] re-generation of '{name}' failed: {ex.Message}"); return (false, before, before); }
            if (gen is null) { Console.WriteLine($"  [reflect] couldn't build a better '{name}'."); return (false, before, before); }

            (int ap, int at) = await ScoreOnAsync(gen.Handler, cap.InputType, tests);
            double after = (double)ap / at;

            if (after > before)
            {
                Registry.Register(cap.Name, cap.Description, cap.ExampleRequest, gen.Handler, cap.InputType, cap.OutputType, cap.Stability);
                TryPersistWinner(cap.Name, cap.Description, cap.ExampleRequest, gen.Source, cap.InputType, cap.OutputType, cap.Stability);
                await SaveAssessmentAsync(cap.Name, after, ap, at, "re-worked into a better implementation");
                await Events.AppendAsync("self-improved",
                    $"re-worked '{cap.Name}': confidence {before.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} → {after.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} (adopted a better implementation)",
                    cap.Name);
                Console.WriteLine($"  [reflect] re-worked '{cap.Name}': {before:0.00} → {after:0.00} — adopted the better version.");
                return (true, before, after);
            }

            await SaveAssessmentAsync(cap.Name, before, bp, bt, "re-work attempt did not beat the original");
            await Events.AppendAsync("self-critique",
                $"re-worked '{cap.Name}': new attempt {after.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} did not beat {before.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} — kept the original",
                cap.Name);
            Console.WriteLine($"  [reflect] '{cap.Name}': new attempt {after:0.00} ≤ current {before:0.00} — kept the original.");
            return (false, before, after);
        }
        finally { _gate.Release(); }
    }

    // ── MOOD / internal drives (sentience ladder, bite 6) ───────────────────────────────
    //
    // A small set of scalar drives computed from REAL signals — the episodic log (recent wins vs
    // setbacks, open gaps) plus current load — surfaced as a mood and used to MODULATE the idle loop.
    // Grounded: it reads how the hive's life has actually been going, never a random or invented feeling.

    /// <summary>Read the hive's current mood from its recent episodic memory and a live load signal
    /// (e.g. the coordinator's in-flight request count). Deterministic — same log + load → same mood.</summary>
    public async Task<Mood> AssessMoodAsync(int liveLoad = 0)
    {
        IReadOnlyList<HiveEvent> recent = await Events.RecentAsync(40);
        int gaps = 0, resolved = 0, wins = 0, setbacks = 0, burst = 0;
        DateTime cutoff = DateTime.UtcNow.AddMinutes(-2);
        foreach (HiveEvent e in recent)
        {
            switch (e.Kind)
            {
                case "gap-noticed": gaps++; setbacks++; break;
                case "curiosity-resolved": resolved++; wins++; break;
                case "capability-commissioned":
                case "self-improved":
                case "deliberation-won": wins++; break;
                case "self-critique":
                    if (e.Summary.Contains("weak")) setbacks++;
                    else if (e.Summary.Contains("solid")) wins++;
                    break;
            }
            if (DateTime.TryParse(e.Timestamp, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime t)
                && t.ToUniversalTime() >= cutoff) burst++;
        }
        int openGaps = Math.Max(0, gaps - resolved);
        return Mood.From(openGaps, wins, setbacks, liveLoad, burst);
    }

    /// <summary>A first-person description of the current mood, spoken as the hive's identity.</summary>
    public async Task<string> DescribeMoodAsync(int liveLoad = 0)
        => (await AssessMoodAsync(liveLoad)).Describe(Identity?.Name ?? IdentityStore.Default.Name);

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
        CapType inputType, CapType outputType, StabilityKind stability = StabilityKind.Stable)
    {
        if (_generator is null || source.Length == 0) return false;
        _generator.PersistShared(name, description, exampleRequest, source, inputType, outputType, stability);
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
        {
            // GAP: a composite needed more than we can auto-fill — record it for curiosity to mine.
            await Events.AppendAsync("gap-noticed", $"couldn't compose \"{question}\" — {missing.Count} capabilities missing", question);
            return CompositionResult.Failed(
                $"cannot compose: {missing.Count} capabilities missing " +
                $"({string.Join(", ", missing.Select(i => $"'{plan.Steps[i]}'"))}) — at most 2 can be generated");
        }

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
/// <see cref="Value"/>, the declared <see cref="Type"/> (so a fact can later feed a typed handler
/// input — not built this bite), and its <see cref="Source"/> provenance ("explicit" = a human
/// stored it, "derived" = auto-cached from a stable capability). The hive's "noun".</summary>
public sealed record Fact(string Key, string Value, CapType Type, string Source);

/// <summary>A curiosity PROPOSAL: a capability the hive offers to build to fill a noticed gap.
/// <see cref="Request"/> is the original unmet input; the rest is the general capability it would
/// commission (after approval) to answer that whole class.</summary>
public sealed record CuriosityProposal(string Request, string Name, string Description, CapType InputType, CapType OutputType, StabilityKind Stability);

/// <summary>A capability's self-assessment (bite 5): its <see cref="Confidence"/> (pass rate over
/// fresh tests), the raw <see cref="Passed"/>/<see cref="Total"/>, and human-readable <see cref="Notes"/>
/// (what failed). The hive's judgment of its own work.</summary>
public sealed record SelfAssessment(string Name, double Confidence, int Passed, int Total, string Notes);

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
