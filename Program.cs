using HAL9001;

// =====================================================================================
// HAL9001 — Step 1: Roslyn compile-and-load core, proven in isolation.
//
// This program holds NO compiled-in reverse logic. Instead it carries the reverser as
// a *string of C# source*, compiles that string into a real assembly at runtime, loads
// it, and calls it. That round-trip is the seed of the self-extending agent: later the
// string won't be hardcoded — it'll come from an LLM.
// =====================================================================================

// The registry that will hold every live handler. Starts empty.
var registry = new HandlerRegistry();

// -------------------------------------------------------------------------------------
// The sample handler, written as plain source TEXT (not compiled with the rest of the
// app). A few things this code MUST get right to compile against our host:
//
//   * `using System;`  — this is a separate compilation, so it does NOT inherit the
//     project's ImplicitUsings. Every namespace it needs must be imported explicitly.
//   * `using HAL9001;` — so it can see the IHandler interface it implements.
//   * a public, parameterless class — RuntimeCompiler finds it by interface and
//     constructs it with Activator (which needs a default constructor).
// -------------------------------------------------------------------------------------
const string reverseHandlerSource = """
    using System;
    using HAL9001;

    // Reverses the characters of the input string.
    public class ReverseHandler : IHandler
    {
        public string Handle(string input)
        {
            char[] characters = input.ToCharArray();
            Array.Reverse(characters);
            return new string(characters);
        }
    }
    """;

Console.WriteLine("HAL9001 — runtime compile-and-load demo");
Console.WriteLine("=======================================");
Console.WriteLine();
Console.WriteLine("Compiling the 'reverse' handler from a source string at runtime...");

// Hand the source string to the Roslyn core. On success it's compiled, loaded,
// instantiated, and sitting in the registry under the name "reverse".
bool ok = RuntimeCompiler.TryCompileAndLoad(
    name: "reverse",
    sourceCode: reverseHandlerSource,
    registry: registry,
    out IHandler? reverseHandler);

if (!ok || reverseHandler is null)
{
    Console.WriteLine();
    Console.WriteLine("Could not compile the sample handler. See errors above. Exiting.");
    return; // Nothing more we can do without our one handler.
}

// -------------------------------------------------------------------------------------
// PROOF IT WORKS: call the just-compiled code on a known input and show the result.
// If you see "1009LAH" below, runtime-generated code just executed in this process.
// -------------------------------------------------------------------------------------
const string proofInput = "HAL9001";
Console.WriteLine();
Console.WriteLine("Proof-of-life — calling the compiled handler:");
Console.WriteLine($"  input    : {proofInput}");
Console.WriteLine($"  reversed : {reverseHandler.Handle(proofInput)}");
Console.WriteLine();

// -------------------------------------------------------------------------------------
// REPL: type something, the app looks in the registry and routes to a handler.
//
// For Step 1 there's exactly one handler, so we just route every line to it (via
// registry.First()) — but notice the routing goes THROUGH the registry, which is the
// same path later steps will use to dispatch among many handlers (and to detect "no
// handler exists yet → go generate one").
// -------------------------------------------------------------------------------------
Console.WriteLine($"Loaded handlers: {string.Join(", ", registry.Names)}");
Console.WriteLine("Type some text and press Enter to run it through the compiled handler.");
Console.WriteLine("Type 'exit' or 'quit' (or just press Enter on an empty line) to stop.");
Console.WriteLine();

while (true)
{
    Console.Write("> ");
    string? line = Console.ReadLine();

    // Null = end-of-input stream (e.g. Ctrl+Z). Empty/exit/quit = user wants out.
    if (line is null ||
        line.Length == 0 ||
        line.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
        line.Equals("quit", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("Goodbye.");
        break;
    }

    // Ask the registry for a handler. Today there's one; this is the dispatch seam.
    IHandler? handler = registry.First();
    if (handler is null)
    {
        Console.WriteLine("  (no handler available)");
        continue;
    }

    Console.WriteLine($"  {handler.Handle(line)}");
}
