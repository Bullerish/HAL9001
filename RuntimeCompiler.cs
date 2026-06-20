using System.Reflection;
using Microsoft.CodeAnalysis;          // SyntaxTree, MetadataReference, Diagnostic, OptimizationLevel...
using Microsoft.CodeAnalysis.CSharp;   // CSharpSyntaxTree, CSharpCompilation, CSharpCompilationOptions
using Microsoft.CodeAnalysis.Emit;     // EmitResult

namespace HAL9001;

/// <summary>
/// The heart of the self-extending agent: take a STRING of C# source code, turn it
/// into a real, runnable assembly in memory, load it into this process, instantiate
/// the handler it defines, and register it — all while the program is running.
///
/// Nothing here is "scripting" in the REPL sense. We use Roslyn's *compilation* API,
/// which is exactly the same compiler that `dotnet build` uses, just driven from code
/// and pointed at an in-memory output instead of a .dll on disk.
/// </summary>
public static class RuntimeCompiler
{
    /// <summary>
    /// Compile <paramref name="sourceCode"/> (which must define a public, non-abstract
    /// class implementing <see cref="IHandler"/>), load it, instantiate it, and add it
    /// to <paramref name="registry"/> under <paramref name="name"/>.
    ///
    /// Returns true on success. On ANY failure — compile errors, no IHandler type found,
    /// an exception while constructing the object — it prints a clear message and returns
    /// false instead of throwing. "Bad generated code" is an expected, recoverable event
    /// in this project, not a crash.
    /// </summary>
    public static bool TryCompileAndLoad(
        string name,
        string sourceCode,
        HandlerRegistry registry,
        out IHandler? handler)
    {
        handler = null;

        try
        {
            // ----------------------------------------------------------------
            // STEP 1 — PARSE the source text into a syntax tree.
            //
            // Roslyn never works on raw text; it works on a parsed tree of nodes
            // (namespaces, classes, methods, statements...). ParseText runs the
            // C# lexer + parser and hands back that tree. Note: parsing succeeds
            // even if the code is semantically nonsense ("foo bar baz" as a body) —
            // real type checking happens later, at Emit time.
            // ----------------------------------------------------------------
            SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);

            // ----------------------------------------------------------------
            // STEP 2 — Gather METADATA REFERENCES.
            //
            // To compile code that says `string`, `Array.Reverse`, or `IHandler`,
            // the compiler must be able to *see* the assemblies those types live in —
            // exactly like adding references to a .csproj. We can't just "use the
            // ones we have" implicitly; we have to hand them to Roslyn explicitly.
            //
            // The robust trick on modern .NET: TRUSTED_PLATFORM_ASSEMBLIES is a
            // path-separated list of every assembly the runtime loaded THIS app with —
            // the whole BCL (System.Private.CoreLib, System.Runtime, System.Console, …)
            // PLUS our own HAL9001.dll. Feeding all of them to the compiler guarantees
            // the generated code can reference anything we can, including our IHandler
            // interface, which lives in this very program.
            // ----------------------------------------------------------------
            string trusted = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;

            // HashSet (case-insensitive) so we never add the same path twice.
            var referencePaths = trusted
                .Split(Path.PathSeparator)
                .Where(p => p.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Belt-and-suspenders: make sure the assembly defining IHandler is in the
            // set. It's normally already there (it's our app), and the HashSet dedupes.
            referencePaths.Add(typeof(IHandler).Assembly.Location);

            var references = referencePaths
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
                .ToList();

            // ----------------------------------------------------------------
            // STEP 3 — Build the COMPILATION.
            //
            // A CSharpCompilation bundles together: an assembly name, the syntax
            // trees to compile, the references above, and options. Two options matter:
            //
            //   * OutputKind.DynamicallyLinkedLibrary — produce a .dll (a library),
            //     not an .exe. Our generated code has no Main(); it's just a class.
            //
            //   * A UNIQUE assembly name (GUID). Once an assembly is loaded into the
            //     default context it stays loaded, and two assemblies with the same
            //     identity would collide. A fresh name per compile sidesteps that —
            //     important later when we compile many handlers in one session.
            // ----------------------------------------------------------------
            string assemblyName = "HAL9001.Generated." + Guid.NewGuid().ToString("N");

            CSharpCompilation compilation = CSharpCompilation.Create(
                assemblyName,
                syntaxTrees: new[] { syntaxTree },
                references: references,
                options: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release));

            // ----------------------------------------------------------------
            // STEP 4 — EMIT (this is where compilation actually happens).
            //
            // Up to now nothing has been compiled — Create() just describes the work.
            // Emit() runs the full pipeline: binding, type checking, IL generation,
            // and writing a complete .NET assembly (a portable executable image) into
            // whatever stream we give it. We hand it a MemoryStream, so the assembly
            // exists only as bytes in RAM — never touching disk.
            //
            // EmitResult.Success tells us whether it produced a valid assembly, and
            // EmitResult.Diagnostics carries every error/warning the compiler found.
            // ----------------------------------------------------------------
            using var assemblyStream = new MemoryStream();
            EmitResult emitResult = compilation.Emit(assemblyStream);

            // ----------------------------------------------------------------
            // STEP 5 — Handle COMPILE ERRORS gracefully.
            //
            // If Emit failed, surface the actual compiler errors (the same CS#### codes
            // you'd see in Visual Studio) and bail out with false. This is the safety
            // net for the whole project: when an LLM later writes broken code, we report
            // it and stay alive instead of crashing.
            // ----------------------------------------------------------------
            if (!emitResult.Success)
            {
                var errors = emitResult.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .ToList();

                Console.WriteLine($"  [compile] FAILED with {errors.Count} error(s):");
                foreach (Diagnostic diagnostic in errors)
                {
                    // Location maps the error back to a line/column in the source text.
                    Console.WriteLine($"    {diagnostic.Id}: {diagnostic.GetMessage()}");
                    Console.WriteLine($"      at {diagnostic.Location.GetLineSpan()}");
                }
                return false;
            }

            // ----------------------------------------------------------------
            // STEP 6 — LOAD the freshly emitted assembly into this process.
            //
            // Rewind the stream and load the raw bytes. Assembly.Load(byte[]) brings
            // the assembly into the default load context, where its types become usable
            // by reflection just like any other loaded type.
            //
            // (Forward note for a later step: this assembly can't be unloaded. To make
            // handlers reloadable/replaceable without leaking, you'd load each into its
            // own collectible AssemblyLoadContext. Overkill for proving the core works.)
            // ----------------------------------------------------------------
            assemblyStream.Seek(0, SeekOrigin.Begin);
            Assembly compiledAssembly = Assembly.Load(assemblyStream.ToArray());

            // ----------------------------------------------------------------
            // STEP 7 — FIND the handler type and INSTANTIATE it.
            //
            // We don't know the class's name — only that it implements IHandler. So we
            // scan the assembly's types for a concrete (non-abstract, non-interface)
            // class assignable to IHandler, then use Activator to call its default
            // constructor. The cast to IHandler is the payoff: from here on the host
            // talks to runtime-generated code purely through the interface.
            // ----------------------------------------------------------------
            Type? handlerType = compiledAssembly.GetTypes()
                .FirstOrDefault(t =>
                    typeof(IHandler).IsAssignableFrom(t) &&
                    !t.IsAbstract &&
                    !t.IsInterface);

            if (handlerType is null)
            {
                Console.WriteLine("  [load] Compiled OK, but found no class implementing IHandler.");
                return false;
            }

            var instance = (IHandler)Activator.CreateInstance(handlerType)!;

            // ----------------------------------------------------------------
            // STEP 8 — REGISTER it. The new capability is now live.
            // ----------------------------------------------------------------
            registry.Register(name, instance);
            handler = instance;

            Console.WriteLine($"  [ok] Compiled '{handlerType.FullName}' and registered it as '{name}'.");
            return true;
        }
        catch (Exception ex)
        {
            // Anything unexpected (e.g. the constructor throwing) lands here. Report and
            // keep the program alive — graceful failure is the whole point.
            Console.WriteLine($"  [error] Unexpected failure while compiling/loading: {ex.Message}");
            return false;
        }
    }
}
