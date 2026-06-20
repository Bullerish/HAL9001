namespace HAL9001;

/// <summary>
/// Step 1 demo, unchanged in behavior — just moved out of Program.cs so the entry
/// point can choose between demos. Compiles a handler from a source string at runtime,
/// proves it works, then runs a small REPL routed through the registry.
/// </summary>
public static class RoslynDemo
{
    public static void Run()
    {
        // The registry that will hold every live handler. Starts empty.
        var registry = new HandlerRegistry();

        // The sample handler, written as plain source TEXT (compiled separately from the
        // app, so it must import every namespace it needs explicitly — including HAL9001
        // so it can see IHandler).
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

        // PROOF: call the just-compiled code on a known input.
        const string proofInput = "HAL9001";
        Console.WriteLine();
        Console.WriteLine("Proof-of-life — calling the compiled handler:");
        Console.WriteLine($"  input    : {proofInput}");
        Console.WriteLine($"  reversed : {reverseHandler.Handle(proofInput)}");
        Console.WriteLine();

        // REPL: route every line through the registry (the same dispatch seam later
        // steps will use to pick among many handlers).
        Console.WriteLine($"Loaded handlers: {string.Join(", ", registry.Names)}");
        Console.WriteLine("Type some text and press Enter to run it through the compiled handler.");
        Console.WriteLine("Type 'exit' or 'quit' (or just press Enter on an empty line) to stop.");
        Console.WriteLine();

        while (true)
        {
            Console.Write("> ");
            string? line = Console.ReadLine();

            if (line is null ||
                line.Length == 0 ||
                line.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("quit", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Goodbye.");
                break;
            }

            IHandler? handler = registry.First();
            if (handler is null)
            {
                Console.WriteLine("  (no handler available)");
                continue;
            }

            Console.WriteLine($"  {handler.Handle(line)}");
        }
    }
}
