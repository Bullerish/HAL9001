// hal9001:name=modular-arithmetic-calculator
// hal9001:description=Compute modular arithmetic operations (addition, subtraction, multiplication, exponentiation) with a given modulus
// hal9001:request=modular arithmetic is neat
// hal9001:intype=String
// hal9001:outtype=String
// hal9001:stability=Stable
using System;
using System.Text.RegularExpressions;
using HAL9001;

public class ModularArithmeticCalculator : IHandler
{
    public string Handle(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "Please provide a modular arithmetic expression (e.g., '5 + 3 mod 7' or '2^10 mod 13').";

        input = input.Trim().ToLower();

        // Extract modulus first
        var modMatch = Regex.Match(input, @"mod\s+(\d+)", RegexOptions.IgnoreCase);
        if (!modMatch.Success)
            return "No modulus found. Please specify 'mod N' in your expression.";

        if (!long.TryParse(modMatch.Groups[1].Value, out long modulus))
            return "Invalid modulus value.";

        if (modulus <= 0)
            return "Modulus must be a positive integer.";

        // Remove the mod N part for parsing operations
        string expression = Regex.Replace(input, @"mod\s+\d+", "", RegexOptions.IgnoreCase).Trim();

        // Try to parse and evaluate
        try
        {
            long result = EvaluateExpression(expression, modulus);
            return $"Result: {result}";
        }
        catch (Exception ex)
        {
            return $"Could not parse expression: {ex.Message}";
        }
    }

    private long EvaluateExpression(string expr, long modulus)
    {
        expr = expr.Trim();

        // Handle exponentiation (highest precedence)
        var expMatch = Regex.Match(expr, @"(\d+)\s*\^\s*(\d+)");
        if (expMatch.Success)
        {
            long baseNum = long.Parse(expMatch.Groups[1].Value);
            long expNum = long.Parse(expMatch.Groups[2].Value);
            long expResult = ModPow(baseNum, expNum, modulus);
            expr = expr.Substring(0, expMatch.Index) + expResult + expr.Substring(expMatch.Index + expMatch.Length);
            return EvaluateExpression(expr, modulus);
        }

        // Handle addition and subtraction (left to right)
        var addSubMatch = Regex.Match(expr, @"(\d+)\s*([\+\-])\s*(\d+)");
        if (addSubMatch.Success)
        {
            long left = long.Parse(addSubMatch.Groups[1].Value);
            string op = addSubMatch.Groups[2].Value;
            long right = long.Parse(addSubMatch.Groups[3].Value);

            long opResult;
            if (op == "+")
                opResult = (left + right) % modulus;
            else
                opResult = ((left - right) % modulus + modulus) % modulus;

            expr = expr.Substring(0, addSubMatch.Index) + opResult + expr.Substring(addSubMatch.Index + addSubMatch.Length);
            return EvaluateExpression(expr, modulus);
        }

        // Handle multiplication
        var multMatch = Regex.Match(expr, @"(\d+)\s*\*\s*(\d+)");
        if (multMatch.Success)
        {
            long left = long.Parse(multMatch.Groups[1].Value);
            long right = long.Parse(multMatch.Groups[2].Value);
            long multResult = (left * right) % modulus;
            expr = expr.Substring(0, multMatch.Index) + multResult + expr.Substring(multMatch.Index + multMatch.Length);
            return EvaluateExpression(expr, modulus);
        }

        // Check if we have a simple number left
        if (long.TryParse(expr, out long finalResult))
            return ((finalResult % modulus) + modulus) % modulus;

        throw new InvalidOperationException($"Could not evaluate: {expr}");
    }

    private long ModPow(long baseNum, long exp, long modulus)
    {
        long result = 1;
        baseNum %= modulus;
        while (exp > 0)
        {
            if (exp % 2 == 1)
                result = (result * baseNum) % modulus;
            baseNum = (baseNum * baseNum) % modulus;
            exp /= 2;
        }
        return result;
    }
}