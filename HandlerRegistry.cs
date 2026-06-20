namespace HAL9001;

/// <summary>
/// An in-memory lookup table of live handlers, keyed by a name.
///
/// In this step there's only one handler (the runtime-compiled string reverser),
/// but the registry is the seam the whole project grows around: when a question
/// arrives we'll look here first, and when we compile a new capability we'll drop
/// it in here so it's instantly available.
/// </summary>
public sealed class HandlerRegistry
{
    // The actual store. Case-insensitive so "Reverse" and "reverse" find the same
    // handler — handy once names come from user input or an LLM.
    private readonly Dictionary<string, IHandler> _handlers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many handlers are currently loaded.</summary>
    public int Count => _handlers.Count;

    /// <summary>The names of all registered handlers.</summary>
    public IEnumerable<string> Names => _handlers.Keys;

    /// <summary>
    /// Add (or replace) a handler under the given name. Replacing matters later:
    /// when a newer, better version of a capability is compiled, it overwrites the old.
    /// </summary>
    public void Register(string name, IHandler handler)
    {
        _handlers[name] = handler;
    }

    /// <summary>
    /// Try to find a handler by name. Returns false (no exception) if it's missing —
    /// "no handler for this" is the normal trigger that will later kick off code
    /// generation, not an error.
    /// </summary>
    public bool TryGet(string name, out IHandler handler) =>
        _handlers.TryGetValue(name, out handler!);

    /// <summary>
    /// Convenience for this early step: grab the single handler we've loaded so the
    /// REPL has something to route to. Returns null if the registry is empty.
    /// </summary>
    public IHandler? First() => _handlers.Values.FirstOrDefault();
}
