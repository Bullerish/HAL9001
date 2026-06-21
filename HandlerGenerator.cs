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
    public async Task<IHandler?> GenerateAsync(
        string name, string description, string exampleRequest, CancellationToken ct = default)
    {
        // The USER prompt carries the specific, volatile part: the capability to build plus
        // a concrete example. The "general, not one-off" rules live in the system prompt.
        string userPrompt =
            $"Build this capability:\n  name: {name}\n  description: {description}\n\n" +
            $"It must handle the whole class of such requests, not just this example.\n" +
            $"Example request it must answer: \"{exampleRequest}\"\n\n" +
            "Output only the raw C# class.";

        string source = CleanSource(await _client.CompleteAsync(SystemPrompt, userPrompt, ct));

        // VALIDATION before we even hand it to the compiler: if it doesn't mention the
        // interface, the model ignored the contract and there's no point compiling.
        if (!LooksLikeHandler(source))
        {
            Console.WriteLine("  [generate] Response doesn't look like an IHandler class. Raw output:");
            Console.WriteLine(Indent(source));
            return null;
        }

        PrintSource("generated source", source);

        // First compile attempt. We capture the diagnostics so we can feed them back.
        if (RuntimeCompiler.TryCompileAndLoad(name, description, source, _registry, out IHandler? handler, out string? errors))
        {
            // Compiled + registered → persist the exact source that compiled, then push.
            PersistAndPush(name, description, exampleRequest, source);
            return handler;
        }

        // ── ONE fix-up retry (capped so it can never loop) ──────────────────────────────
        Console.WriteLine();
        Console.WriteLine("  [generate] Didn't compile — sending the errors back for one fix attempt...");

        string fixPrompt =
            "The C# you wrote did not compile.\n\n" +
            "--- your code ---\n" + source + "\n\n" +
            "--- compiler errors ---\n" + errors + "\n\n" +
            "Return the corrected class. Output only raw C#, no fences or commentary.";

        string fixedSource = CleanSource(await _client.CompleteAsync(SystemPrompt, fixPrompt, ct));
        PrintSource("regenerated source", fixedSource);

        if (RuntimeCompiler.TryCompileAndLoad(name, description, fixedSource, _registry, out handler, out _))
        {
            PersistAndPush(name, description, exampleRequest, fixedSource);
            return handler;
        }

        Console.WriteLine("  [generate] Still didn't compile after one fix attempt. Giving up on this request.");
        return null;
    }

    // =====================================================================================
    // PERSIST + PUSH (Step 4, push-half)
    //
    // Only ever called AFTER a handler compiled and registered — failed code never reaches
    // here, so the repo only ever gets working handlers. Writes one .cs file per handler to
    // handlers/, then commits + pushes just that file. A push failure is reported but never
    // fatal: the handler is already live in memory for this session.
    // =====================================================================================
    private void PersistAndPush(string name, string description, string exampleRequest, string source)
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
                $"// hal9001:request={OneLine(exampleRequest)}\n";
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

    /// <summary>
    /// After answering, ask the model for one natural follow-up question. For now we just
    /// print it locally — later it's what two instances will volley back and forth.
    /// </summary>
    public async Task<string> GenerateFollowUpAsync(string request, string answer, CancellationToken ct = default)
    {
        const string system =
            "Given a user's request and the answer they got, propose ONE short, natural " +
            "follow-up question that continues the conversation. Output only the question.";
        string user = $"Request: {request}\nAnswer: {answer}\n\nYour follow-up question:";
        return (await _client.CompleteAsync(system, user, ct)).Trim();
    }

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
