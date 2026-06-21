using System.Text;
using System.Text.RegularExpressions;

namespace HAL9001;

/// <summary>
/// Commissions a GENERAL, reusable capability (Rung 1a): given a capability name +
/// description (chosen by the <see cref="CapabilityRouter"/>) and an example request, it
///   1. asks the LLM to write the C# class — the reusable method, never the answer,
///   2. cleans + validates the response,
///   3. compiles it through <see cref="RuntimeCompiler"/> (registering it under the name +
///      description so the router can reuse it),
///   4. on a compile failure, sends the errors back for ONE fix-up attempt, and
///   5. on success, writes the source to handlers/ and pushes it (via <see cref="GitSync"/>)
///      so other instances can pull and reuse the same capability.
/// </summary>
public sealed class HandlerGenerator
{
    private readonly AnthropicClient _client;
    private readonly HandlerRegistry _registry;
    private readonly GitSync? _git;

    // Handlers persisted+pushed this session, so a repeated request never makes a second
    // commit. (In practice the REPL's in-memory cache already prevents re-generation; this
    // is an explicit second guard against duplicate commits.)
    private readonly HashSet<string> _persisted = new(StringComparer.OrdinalIgnoreCase);

    public HandlerGenerator(AnthropicClient client, HandlerRegistry registry, GitSync? git = null)
    {
        _client = client;
        _registry = registry;
        _git = git;
    }

    // =====================================================================================
    // PROMPT CONSTRUCTION
    //
    // The SYSTEM prompt is fixed: it encodes the non-negotiable rules every generated
    // handler must follow. It's where we (a) hand the model the exact IHandler contract so
    // it implements the right interface, and (b) demand RAW code only — no markdown fences,
    // no prose — because anything extra would break compilation. Keeping these rules in the
    // system prompt (not the per-request prompt) means they're stated once, consistently.
    // =====================================================================================
    private const string SystemPrompt = """
        You generate C# source code for a plugin system. You output CODE ONLY — you never
        answer the user's question yourself; you write the reusable METHOD that finds the
        answer when run.

        STRICT output rules:
        - Output ONLY raw C# source. No markdown, no ``` fences, no prose. The first
          character must begin the C# code.
        - Define exactly ONE public class with a public parameterless constructor.
        - It MUST implement this interface (already in the host program):
              namespace HAL9001 { public interface IHandler { string Handle(string input); } }
        - Start with `using System;` and `using HAL9001;` plus any usings you need
          (System.Collections.Generic, System.Linq, System.Net.Http, System.Text.Json, ...).

        Build the GENERAL, reusable capability — not a one-off:
        - Handle the WHOLE CLASS of such requests, not just the one example. Parse the
          specific item out of `input` (be tolerant of phrasing and casing).
        - Get the data whichever way fits: BAKE IN a dataset when it's small and stable
          (e.g. US state capitals), OR QUERY an appropriate web source when it's large or
          changes over time. If you call the network, use HttpClient with a short timeout
          (a few seconds) and return a clear message if the call fails.
        - Handle(input) returns the answer as a string. Be robust to missing/unknown input.
        """;

    /// <summary>
    /// Commission a GENERAL capability: ask the LLM to write the reusable method for
    /// <paramref name="description"/>, compile it, register it under <paramref name="name"/>
    /// with that description, and push it. <paramref name="exampleRequest"/> is the request
    /// that triggered this — passed to the model as a concrete example it must handle.
    /// Returns the live handler, or null if it never compiled.
    /// </summary>
    public async Task<GeneratedHandler?> GenerateAsync(
        string name, string description, string exampleRequest, CancellationToken ct = default,
        bool persist = true,
        CapType inputType = CapType.String, CapType outputType = CapType.String)
    {
        // The "general, not one-off" rules live in the system prompt; this carries the
        // specific capability to build plus a concrete example AND its declared types. The types
        // tell the LLM exactly what to parse from the input and what to produce — this is what
        // makes a handler robust to phrasing (e.g. an Int handler copes with "7th").
        string basePrompt =
            $"Build this capability:\n  name: {name}\n  description: {description}\n" +
            $"  input type: {CapTypes.Name(inputType)} — {CapTypes.Hint(inputType)}\n" +
            $"  output type: {CapTypes.Name(outputType)} — {CapTypes.Hint(outputType)}\n\n" +
            $"It must handle the whole class of such requests, not just this example.\n" +
            $"Parse the input as {CapTypes.Name(inputType)} robustly; if the input has no valid " +
            $"{CapTypes.Name(inputType)}, return a short, clear message saying so.\n" +
            $"Example request it must answer: \"{exampleRequest}\"\n\n" +
            "Output only the raw C# class.";

        string? priorSource = null;
        string? priorFailure = null;

        // Up to 2 attempts: initial + one fix-up. A "failure" is either a compile error OR a
        // runtime throw on the trial run — both get fed back to the model the same way, so a
        // handler that compiles but crashes (e.g. a duplicate dictionary key) gets a fix too.
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            string prompt = priorFailure is null
                ? basePrompt
                : "The C# you wrote failed.\n\n--- your code ---\n" + priorSource +
                  "\n\n--- the failure ---\n" + priorFailure +
                  "\n\nReturn a corrected version. Output only raw C#, no fences or commentary.";

            string source = CleanSource(await _client.CompleteAsync(SystemPrompt, prompt, ct));
            priorSource = source;

            // Cheap pre-check: if it doesn't even mention the interface, don't bother compiling.
            if (!LooksLikeHandler(source))
            {
                Console.WriteLine("  [generate] Response doesn't look like an IHandler class.");
                Console.WriteLine(Indent(source));
                priorFailure = "Output was not a C# class implementing IHandler.";
                continue;
            }

            PrintSource(attempt == 1 ? "generated source" : "regenerated source", source);

            // 1) Must compile.
            if (!RuntimeCompiler.TryCompileAndLoad(name, description, exampleRequest, source, _registry, out IHandler? handler, out string? compileErrors, inputType, outputType))
            {
                Console.WriteLine("  [generate] didn't compile — feeding the errors back for a fix...");
                priorFailure = compileErrors;
                continue;
            }

            // 2) Must actually RUN. Trial it on the example so we never push code that
            //    compiles but throws at runtime.
            string? runtimeError = await TrialRunAsync(handler!, exampleRequest);
            if (runtimeError is not null)
            {
                Console.WriteLine($"  [generate] compiled but threw at runtime ({runtimeError}) — feeding it back for a fix...");
                priorFailure = "It compiled but threw at runtime on the example: " + runtimeError;
                continue;
            }

            // Compiles AND runs. Persist + push ONLY for the real answer path; a rung-5a
            // candidate (persist:false) is generated and held locally — NOT pushed — so N
            // competing nodes don't spam the repo with rival implementations (only 5b's winner
            // should propagate). We return the SOURCE alongside the handler so the swarm can push
            // the exact winning implementation later (rung 5b) without regenerating it.
            if (persist)
                PersistAndPush(name, description, exampleRequest, source, inputType, outputType);
            return new GeneratedHandler(handler!, source);
        }

        Console.WriteLine("  [generate] couldn't produce a working capability after a retry. Giving up.");
        return null;
    }

    // Run the handler once on the example, guarded, to surface runtime failures (a thrown
    // exception, a duplicate-key dictionary, a hung network call) BEFORE we persist + push.
    // Returns null if it ran cleanly, or the error message to feed back to the model.
    private static async Task<string?> TrialRunAsync(IHandler handler, string input)
    {
        try
        {
            await Task.Run(() => handler.Handle(input)).WaitAsync(TimeSpan.FromSeconds(20));
            return null;
        }
        catch (TimeoutException)
        {
            return "ran longer than 20s (likely a slow or hung network call)";
        }
        catch (Exception ex)
        {
            return ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// Push an EXTERNALLY-chosen implementation (rung 5b: the deliberation winner). Same write +
    /// commit + push as the normal answer path — used by the coordinator to propagate exactly the
    /// one winning candidate's source, after the losers were generated locally and discarded.
    /// </summary>
    public void PersistShared(string name, string description, string exampleRequest, string source,
        CapType inputType = CapType.String, CapType outputType = CapType.String)
        => PersistAndPush(name, description, exampleRequest, source, inputType, outputType);

    // =====================================================================================
    // PERSIST + PUSH (Step 4, push-half)
    //
    // Only ever called AFTER a handler compiled and registered — failed code never reaches
    // here, so the repo only ever gets working handlers. Writes one .cs file per handler to
    // handlers/, then commits + pushes just that file. A push failure is reported but never
    // fatal: the handler is already live in memory for this session.
    // =====================================================================================
    private void PersistAndPush(string name, string description, string exampleRequest, string source,
        CapType inputType = CapType.String, CapType outputType = CapType.String)
    {
        if (_git is null)
        {
            Console.WriteLine("  [sync] no git repo detected — handler kept in memory only.");
            return;
        }

        if (_persisted.Contains(name))
            return; // already saved this session; don't make a duplicate commit

        try
        {
            Directory.CreateDirectory(_git.HandlersDirectory);

            // Unique filename: descriptive base (the generated class name) + short GUID, so
            // two instances generating the same capability don't collide on a filename.
            string fileBase = DeriveFileBase(source, name);
            string unique = Guid.NewGuid().ToString("N")[..8];
            string fileName = $"{fileBase}_{unique}.cs";
            string fullPath = Path.Combine(_git.HandlersDirectory, fileName);

            // Embed the capability's name AND description as a header so the OTHER instance
            // can register it under the same name with the same description when it pulls —
            // that's what lets its router recognize and reuse the capability. Comments are
            // ignored by the compiler, so the loaded handler is byte-for-byte equivalent.
            string header =
                $"// hal9001:name={name}\n" +
                $"// hal9001:description={OneLine(description)}\n" +
                $"// hal9001:request={OneLine(exampleRequest)}\n" +
                $"// hal9001:intype={CapTypes.Name(inputType)}\n" +
                $"// hal9001:outtype={CapTypes.Name(outputType)}\n";
            File.WriteAllText(fullPath, header + source);
            Console.WriteLine($"  [sync] wrote handlers/{fileName}");

            if (_git.CommitAndPushFile(fullPath, $"Add handler: {name}"))
            {
                _persisted.Add(name);
                Console.WriteLine($"  [sync] pushed handlers/{fileName} to GitHub.");
            }
        }
        catch (Exception ex)
        {
            // Disk/IO or anything unexpected — never let persistence crash the agent.
            Console.WriteLine($"  [sync] could not persist/push handler: {ex.Message}");
        }
    }

    // Collapse a request to a single comment-safe line for the file header.
    private static string OneLine(string text) =>
        text.Replace("\r", " ").Replace("\n", " ").Trim();

    // Use the generated class name as the file's base (e.g. "WordOrderReverser"); fall back
    // to a PascalCase version of the registry name if no class declaration is found.
    private static string DeriveFileBase(string source, string fallbackName)
    {
        Match m = Regex.Match(source, @"\bclass\s+([A-Za-z_][A-Za-z0-9_]*)");
        if (m.Success) return m.Groups[1].Value;

        var sb = new StringBuilder();
        foreach (string part in fallbackName.Split('-', StringSplitOptions.RemoveEmptyEntries))
            sb.Append(char.ToUpperInvariant(part[0])).Append(part.AsSpan(1));
        return sb.Length > 0 ? sb.ToString() : "Handler";
    }

    // (Follow-up questions are no longer generated by the LLM. The APP generates them from
    //  its own capability catalog — see AgentRepl.BuildFollowUp — so the LLM stays purely a
    //  toolsmith and the conversation is driven by what the agent can actually do.)

    // =====================================================================================
    // RESPONSE STRIPPING / VALIDATION
    //
    // Even with "no fences" in the system prompt, models occasionally wrap code in
    // ```csharp ... ```. CleanSource removes that defensively so a stray fence doesn't turn
    // into a compile error. We keep the cleaning minimal on purpose — the goal is to show
    // you what the model produced, not to silently rewrite it.
    // =====================================================================================
    private static string CleanSource(string raw)
    {
        string s = raw.Trim();

        if (s.StartsWith("```"))
        {
            // Drop the opening fence line (handles ``` and ```csharp).
            int firstNewline = s.IndexOf('\n');
            if (firstNewline >= 0) s = s[(firstNewline + 1)..];

            // Drop the closing fence if present.
            int lastFence = s.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence >= 0) s = s[..lastFence];
        }

        return s.Trim();
    }

    // A cheap sanity gate: real handler source must reference the interface and a class.
    private static bool LooksLikeHandler(string source) =>
        source.Contains("IHandler") && source.Contains("class");

    private static void PrintSource(string label, string source)
    {
        Console.WriteLine();
        Console.WriteLine($"  ----- {label} -----");
        Console.WriteLine(Indent(source));
        Console.WriteLine("  --------------------");
    }

    private static string Indent(string text) =>
        "    " + text.Replace("\n", "\n    ");
}

/// <summary>A freshly generated capability: the live handler plus the exact source it was compiled
/// from (so the swarm can push the winning implementation later without regenerating it).</summary>
public sealed record GeneratedHandler(IHandler Handler, string Source);
