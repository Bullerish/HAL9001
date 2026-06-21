namespace HAL9001;

/// <summary>
/// One registered capability: a name, a one-line description of the GENERAL class of
/// requests it handles, and the compiled handler that does the work. The description is
/// what the <see cref="CapabilityRouter"/> reads to decide whether an incoming request is
/// already covered.
/// </summary>
public sealed record Capability(string Name, string Description, IHandler Handler);

/// <summary>
/// In-memory catalog of live capabilities, keyed by name.
///
/// As of Rung 1a this is no longer "one handler per literal question." Each entry is a
/// general capability (e.g. "state-capitals": answers the capital of any US state), and the
/// router matches a request to one of these by MEANING rather than by exact string.
/// </summary>
public sealed class HandlerRegistry
{
    // Case-insensitive so names from the LLM / user resolve consistently.
    private readonly Dictionary<string, Capability> _capabilities =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many capabilities are loaded.</summary>
    public int Count => _capabilities.Count;

    /// <summary>The names of all registered capabilities.</summary>
    public IEnumerable<string> Names => _capabilities.Keys;

    /// <summary>
    /// Add (or replace) a capability. Replacing matters later: when a better version of a
    /// capability is compiled, it overwrites the old one under the same name.
    /// </summary>
    public void Register(string name, string description, IHandler handler)
    {
        _capabilities[name] = new Capability(name, description, handler);
    }

    /// <summary>Try to find a handler by capability name.</summary>
    public bool TryGet(string name, out IHandler handler)
    {
        if (_capabilities.TryGetValue(name, out Capability? capability))
        {
            handler = capability.Handler;
            return true;
        }
        handler = null!;
        return false;
    }

    /// <summary>
    /// The catalog the router reasons over: every capability's name + description. This is
    /// how the agent "knows what it can already do" before deciding to build something new.
    /// </summary>
    public IReadOnlyList<Capability> Catalog() => _capabilities.Values.ToList();

    /// <summary>Convenience: the first handler (used by the Step 1 demo's single-handler REPL).</summary>
    public IHandler? First() => _capabilities.Values.Select(c => c.Handler).FirstOrDefault();
}
