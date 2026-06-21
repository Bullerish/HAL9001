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
            if (decision.Action == RouteAction.UseExisting && Registry.TryGet(decision.Name, out handler))
            {
                usedName = decision.Name;
                Console.WriteLine($"  (using capability '{usedName}')");
            }
            else
            {
                // CreateNew — or a UseExisting that named something we don't actually have
                // (an LLM slip): commission a general capability either way.
                string capName = decision.Name.Length > 0 ? decision.Name : "capability";
                string capDesc = decision.Description.Length > 0 ? decision.Description : request;
                usedName = capName;
                Console.WriteLine($"  (commissioning '{capName}': {capDesc})");
                GeneratedHandler? gen;
                try
                {
                    gen = await _generator!.GenerateAsync(capName, capDesc, request, ct);
                }
                catch (Exception ex)
                {
                    return AnswerResult.GenerationFailed($"(generation failed: {ex.Message})");
                }
                if (gen is null)
                    return AnswerResult.GenerationFailed("(couldn't build a working handler)");
                handler = gen.Handler;
            }

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
    /// Ask the LLM for a few small test cases for the capability a question implies — input →
    /// expected-substring pairs the correct answer must contain. Used by the coordinator when it
    /// fans a novel question out to the swarm, so each candidate can be checked the same way (5b
    /// will score with them). Best-effort: returns an empty list if there's no key or the reply
    /// doesn't parse — candidates are still collected, just without test results.
    /// </summary>
    public async Task<IReadOnlyList<TestCase>> GenerateTestCasesAsync(string question, CancellationToken ct = default)
    {
        if (_client is null) return Array.Empty<TestCase>();

        const string sys = """
            You write a few SMALL test cases for a tool that answers questions like the one given.
            Output ONLY a JSON array (no prose, no fences) of 2-3 objects, each:
              {"input":"<a concrete request the tool should handle>",
               "expected":"<the key substring the correct answer MUST contain — a number, a word, or yes/no>"}
            Choose inputs with clear, unambiguous answers and keep "expected" short and exact.
            """;
        try
        {
            string raw = await _client.CompleteAsync(sys, $"Question: \"{question}\"\nJSON:", ct);
            string json = StripFences(raw);
            using JsonDocument doc = JsonDocument.Parse(json);
            var list = new List<TestCase>();
            foreach (JsonElement e in doc.RootElement.EnumerateArray())
            {
                string input = e.TryGetProperty("input", out var i) ? i.GetString() ?? "" : "";
                string expected = e.TryGetProperty("expected", out var x) ? x.GetString() ?? "" : "";
                if (input.Length > 0 && expected.Length > 0) list.Add(new TestCase(input, expected));
                if (list.Count == 4) break;
            }
            return list;
        }
        catch { return Array.Empty<TestCase>(); }
    }

    /// <summary>
    /// Produce a COMPETING candidate (rung 5a): route the request, reuse a matching handler or
    /// commission a fresh one — but NEVER push it (persist:false), so N nodes generating rivals
    /// don't spam the repo. Then run the chosen handler on the question for the candidate answer,
    /// and on each test case for a pass/fail (output contains the expected substring). Held
    /// locally; the winner's push is a 5b concern.
    /// </summary>
    public async Task<Candidate> ProduceCandidateAsync(string request, IReadOnlyList<TestCase> tests, CancellationToken ct = default)
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
                Console.WriteLine($"  (candidate: commissioning '{capName}' — held locally, not pushed)");
                GeneratedHandler? gen;
                try { gen = await _generator!.GenerateAsync(capName, capDesc, request, ct, persist: false); }
                catch (Exception ex) { return new Candidate(CandidateStatus.GenerationFailed, $"(generation failed: {ex.Message})", capName, capDesc, "", Array.Empty<TestResult>()); }
                if (gen is null) return new Candidate(CandidateStatus.GenerationFailed, "(couldn't build a working handler)", capName, capDesc, "", Array.Empty<TestResult>());
                handler = gen.Handler;
                source = gen.Source;
            }

            string answer = await RunHandlerAsync(handler!, request);
            var results = new List<TestResult>(tests.Count);
            foreach (TestCase tc in tests)
            {
                string got = await RunHandlerAsync(handler!, tc.Input);
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
    /// candidate's source so it becomes the swarm's canonical handler. No-op without a key/generator.
    /// </summary>
    public bool TryPersistWinner(string name, string description, string exampleRequest, string source)
    {
        if (_generator is null || source.Length == 0) return false;
        _generator.PersistShared(name, description, exampleRequest, source);
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
