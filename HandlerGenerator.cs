namespace HAL9001;

/// <summary>
/// Turns a natural-language request into a working, compiled <see cref="IHandler"/> by:
///   1. asking the LLM to write a C# class,
///   2. cleaning + validating the response,
///   3. compiling it through <see cref="RuntimeCompiler"/>,
///   4. and, if that fails, sending the compiler errors back for ONE fix-up attempt.
///
/// This is the self-extension step running locally on one instance — no socket involved.
/// </summary>
public sealed class HandlerGenerator
{
    private readonly AnthropicClient _client;
    private readonly HandlerRegistry _registry;

    public HandlerGenerator(AnthropicClient client, HandlerRegistry registry)
    {
        _client = client;
        _registry = registry;
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
        You generate C# source code for a plugin system. You output CODE ONLY.

        STRICT output rules:
        - Output ONLY raw C# source. No markdown, no ``` fences, no comments-as-explanation,
          no prose before or after. The very first character must begin the C# code.
        - Define exactly ONE public class with a public parameterless constructor.
        - The class MUST implement this interface, which already exists in the host program:
              namespace HAL9001 { public interface IHandler { string Handle(string input); } }
        - Start the file with `using System;` and `using HAL9001;` plus any other System.*
          usings you need.
        - Use ONLY the .NET base class library (System.*). No NuGet packages, no file system,
          no network, no Console output. Handle(input) must just compute and return a string.
        - `input` is the user's request text. Return a helpful string answer for it.
        """;

    /// <summary>
    /// Generate, compile, and register a handler named <paramref name="name"/> for
    /// <paramref name="request"/>. Returns the live handler, or null if it never compiled.
    /// </summary>
    public async Task<IHandler?> GenerateAsync(string name, string request, CancellationToken ct = default)
    {
        // The USER prompt carries the specific, volatile part: the actual request.
        string userPrompt =
            $"Write a handler whose Handle(input) answers requests like this:\n\n\"{request}\"\n\n" +
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
        if (RuntimeCompiler.TryCompileAndLoad(name, source, _registry, out IHandler? handler, out string? errors))
        {
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

        if (RuntimeCompiler.TryCompileAndLoad(name, fixedSource, _registry, out handler, out _))
        {
            return handler;
        }

        Console.WriteLine("  [generate] Still didn't compile after one fix attempt. Giving up on this request.");
        return null;
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
