// hal9001:name=number-base-converter
// hal9001:description=Convert numbers between different bases (binary, octal, decimal, hexadecimal, and arbitrary bases)
// hal9001:request=i really love converting between number bases
// hal9001:intype=String
// hal9001:outtype=String
// hal9001:stability=Stable
using System;
using System.Globalization;
using System.Text.RegularExpressions;
using HAL9001;

public class NumberBaseConverter : IHandler
{
    public string Handle(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "Please provide a number and base conversion request (e.g., 'convert 255 from decimal to binary').";

        input = input.ToLowerInvariant().Trim();

        // Try to extract a number and base conversion pattern
        // Patterns: "convert X from BASE to BASE", "X in BASE", "X to BASE", etc.
        var numberMatch = ExtractConversion(input);

        if (numberMatch == null)
            return "I couldn't find a clear number and base conversion request. Try: 'convert 255 from decimal to hex' or '1010 binary to decimal'.";

        try
        {
            var result = ConvertNumber(numberMatch.Value.number, numberMatch.Value.fromBase, numberMatch.Value.toBase);
            return result;
        }
        catch (Exception ex)
        {
            return $"Conversion failed: {ex.Message}";
        }
    }

    private (string number, string fromBase, string toBase)? ExtractConversion(string input)
    {
        // Pattern: "convert|from X (base) to|in Y (base)" 
        var convertPattern = @"(?:convert\s+)?([0-9a-f]+)\s+(?:in|from)?\s*([a-z]+)?\s+(?:to|in)\s+([a-z]+)";
        var match = Regex.Match(input, convertPattern, RegexOptions.IgnoreCase);

        if (match.Success)
        {
            var number = match.Groups[1].Value;
            var fromBase = match.Groups[2].Value.Length > 0 ? match.Groups[2].Value : "decimal";
            var toBase = match.Groups[3].Value;
            return (number, NormalizeBaseName(fromBase), NormalizeBaseName(toBase));
        }

        // Simpler pattern: "X base1 base2"
        var simplePattern = @"([0-9a-f]+)\s+([a-z]+)\s+([a-z]+)";
        match = Regex.Match(input, simplePattern, RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var number = match.Groups[1].Value;
            var fromBase = NormalizeBaseName(match.Groups[2].Value);
            var toBase = NormalizeBaseName(match.Groups[3].Value);
            return (number, fromBase, toBase);
        }

        return null;
    }

    private string NormalizeBaseName(string baseName)
    {
        return baseName switch
        {
            "bin" or "binary" or "b" => "binary",
            "oct" or "octal" or "o" => "octal",
            "dec" or "decimal" or "d" => "decimal",
            "hex" or "hexadecimal" or "h" => "hexadecimal",
            _ => baseName
        };
    }

    private int GetBaseValue(string baseName)
    {
        return baseName switch
        {
            "binary" => 2,
            "octal" => 8,
            "decimal" => 10,
            "hexadecimal" => 16,
            _ => throw new ArgumentException($"Unknown base: {baseName}")
        };
    }

    private string ConvertNumber(string number, string fromBase, string toBase)
    {
        int from = GetBaseValue(fromBase);
        int to = GetBaseValue(toBase);

        // Convert from source base to decimal
        long decimalValue;
        try
        {
            decimalValue = Convert.ToInt64(number, from);
        }
        catch
        {
            throw new ArgumentException($"'{number}' is not a valid {fromBase} number.");
        }

        // Convert from decimal to target base
        string result;
        if (to == 2)
            result = Convert.ToString(decimalValue, 2);
        else if (to == 8)
            result = Convert.ToString(decimalValue, 8);
        else if (to == 10)
            result = decimalValue.ToString();
        else if (to == 16)
            result = Convert.ToString(decimalValue, 16).ToUpper();
        else
            throw new ArgumentException($"Unsupported base: {toBase}");

        return $"{number} ({fromBase}) = {result} ({toBase})";
    }
}