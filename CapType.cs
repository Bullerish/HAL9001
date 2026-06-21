using System.Text.RegularExpressions;

namespace HAL9001;

/// <summary>
/// The fixed, minimal set of capability types (the typed-capabilities rung — deliberately NOT a
/// full type system). A capability declares one input type and one output type from this set.
/// No custom types, no generics, no coercion — those are later rungs.
/// </summary>
public enum CapType
{
    String, // free-form text (the default; also what untyped/grandfathered handlers are)
    Int,    // a whole number
    Number, // an integer or decimal
    Bool,   // yes/no
    Date,   // a calendar date
}

/// <summary>Helpers for the small type set: parse a name, describe it to the LLM, and do the
/// lightweight boundary parse-check that catches an obvious input mismatch.</summary>
public static class CapTypes
{
    /// <summary>Map a loose string (from the LLM or a file header) to a <see cref="CapType"/>;
    /// anything unrecognized falls back to String, so unknown/old metadata is safe.</summary>
    public static CapType Parse(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "int" or "integer" => CapType.Int,
        "number" or "double" or "decimal" or "float" => CapType.Number,
        "bool" or "boolean" => CapType.Bool,
        "date" or "datetime" => CapType.Date,
        _ => CapType.String,
    };

    public static string Name(CapType t) => t.ToString();

    /// <summary>A one-line instruction for the generation prompt: what the handler should parse
    /// from its input / produce as its output. This is the real payoff of typing — it tells the
    /// LLM to robustly extract (say) an integer instead of regexing raw text and choking on "7th".</summary>
    public static string Hint(CapType t) => t switch
    {
        CapType.Int => "an integer — parse the integer out of the request, tolerant of phrasing like \"7th\", \"number 7\", or \"the seventh\"",
        CapType.Number => "a number (integer or decimal) — parse it tolerantly out of the request",
        CapType.Bool => "a yes/no boolean — answer clearly yes or no",
        CapType.Date => "a date — parse a date out of the request, accepting common formats",
        _ => "free-form text",
    };

    /// <summary>
    /// Boundary parse-check: could a value of this type plausibly be found in the raw input?
    /// Deliberately minimal — only the cases where a mismatch is clear and common (numeric input
    /// with no number). Bool/Date/String are lenient so we never falsely reject a real request.
    /// </summary>
    public static bool Matches(CapType t, string input) => t switch
    {
        CapType.Int => Regex.IsMatch(input, @"-?\d+"),
        CapType.Number => Regex.IsMatch(input, @"-?\d+(\.\d+)?"),
        CapType.Date => DateTime.TryParse(input, out _) || Regex.IsMatch(input, @"\d")
                        || Regex.IsMatch(input, @"(?i)\b(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec|today|tomorrow|yesterday)"),
        CapType.Bool => true,   // boolean INPUT is unusual; don't over-reject
        _ => true,              // String accepts anything
    };

    /// <summary>The clean, typed error returned when input doesn't match the declared input type.</summary>
    public static string Mismatch(CapType t, string input) =>
        $"(type mismatch: this capability expects {Name(t)} input, but found no {Name(t).ToLowerInvariant()} in \"{input}\")";
}
