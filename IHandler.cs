namespace HAL9001;

/// <summary>
/// The contract every handler must implement.
///
/// This interface is the bridge between the host app and the code we compile at
/// runtime. The generated handler lives in a brand-new assembly that the host has
/// never seen before — but because that assembly references THIS one and implements
/// IHandler, the host can hold the new object as an <see cref="IHandler"/> and call
/// <see cref="Handle"/> without knowing anything about its concrete type.
///
/// Keep it deliberately tiny: one method in, one string out. The simpler the
/// contract, the simpler the code an LLM (in a later step) has to generate.
/// </summary>
public interface IHandler
{
    /// <summary>
    /// Process an input string and return a response string.
    /// </summary>
    string Handle(string input);
}
