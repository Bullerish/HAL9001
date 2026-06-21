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

            // Prefer the embedded headers the generator writes. The name keys the registry,
            // the description is what the router reads, and the request is the example the app
            // replays as a follow-up. Files pushed before a header existed fall back / blank.
            string name = ExtractField(source, "name") ?? NameFromFileName(file);
            string description = ExtractField(source, "description") ?? "";
            string example = ExtractField(source, "request") ?? "";

            // RuntimeCompiler registers the capability on success; on failure it prints the
            // CS#### errors itself. A bad file is skipped, never fatal.
            if (RuntimeCompiler.TryCompileAndLoad(name, description, example, source, registry, out _))
                Console.WriteLine($"  [load] {Path.GetFileName(file)} -> '{name}'");
            else
                Console.WriteLine($"  [load] skipped {Path.GetFileName(file)} (did not compile)");
        }
    }

    // Pull a "// hal9001:<field>=<value>" header value out of the source, if present.
    private static string? ExtractField(string source, string field)
    {
        Match m = Regex.Match(source, $@"//\s*hal9001:{field}=([^\r\n]+)");
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
