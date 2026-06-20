using System.Text.RegularExpressions;

namespace HAL9001;

/// <summary>
/// Loads persisted handlers from the handlers/ folder into the registry — the in-process
/// mirror of the push-half. After a <see cref="GitSync.Pull"/>, every .cs file here is
/// compiled (by the same Roslyn pipeline that built it originally) and registered, so this
/// instance gains every capability another instance has generated and pushed.
///
/// No LLM is involved: loading and running an existing handler is pure compile + reflect.
/// </summary>
public static class HandlerLoader
{
    /// <summary>Compile + register every handler in <paramref name="handlersDir"/>.</summary>
    public static void LoadAll(string handlersDir, HandlerRegistry registry)
    {
        if (!Directory.Exists(handlersDir))
            return; // nothing pulled yet — fine

        foreach (string file in Directory.EnumerateFiles(handlersDir, "*.cs").OrderBy(f => f))
        {
            string source = File.ReadAllText(file);

            // The registry name must match what a request would hash to, so prefer the
            // embedded "// hal9001:name=<slug>" header the generator writes. Files pushed
            // before that header existed fall back to the filename (minus the _<guid> suffix).
            string name = ExtractName(source) ?? NameFromFileName(file);

            // RuntimeCompiler registers the handler on success; on failure it prints the
            // CS#### errors itself. A bad file is skipped, never fatal.
            if (RuntimeCompiler.TryCompileAndLoad(name, source, registry, out _))
                Console.WriteLine($"  [load] {Path.GetFileName(file)} -> '{name}'");
            else
                Console.WriteLine($"  [load] skipped {Path.GetFileName(file)} (did not compile)");
        }
    }

    // Pull the slug out of "// hal9001:name=<slug>" if present.
    private static string? ExtractName(string source)
    {
        Match m = Regex.Match(source, @"//\s*hal9001:name=([^\r\n]+)");
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    // Fallback for pre-header files: "VowelCountHandler_d58a3efa.cs" -> "VowelCountHandler".
    private static string NameFromFileName(string file)
    {
        string baseName = Path.GetFileNameWithoutExtension(file);
        int underscore = baseName.LastIndexOf('_');
        return underscore > 0 ? baseName[..underscore] : baseName;
    }
}
