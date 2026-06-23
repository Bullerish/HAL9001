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
            return "Please provide a modular arithmetic expression (e.g., '5 + 3 mod 7' or '2^8 mod 5').";

        input = input.ToLower().Trim();

        if (input.Contains("modular arithmetic is neat"))
            return "Indeed! Modular arithmetic is the foundation of cryptography, number theory, and clock arithmetic. " +
                   "I can compute operations like (a + b) mod n, (a - b) mod n, (a * b) mod n, and (a^b) mod n.";

        var modMatch = Regex.Match(input, @"mod\s+(\d+)", RegexOptions.IgnoreCase);
        if (!modMatch.Success)
            return "No modulus found. Please specify 'mod n' (e.g., '7 + 5 mod 12').";

        if (!int.TryParse(modMatch.Groups[1].Value, out int modulus) || modulus <= 0)
            return "Invalid modulus. Please provide a positive integer after 'mod'.";

        var exprPart = input.Substring(0, modMatch.Index).Trim();

        var powMatch = Regex.Match(exprPart, @"(\d+)\s*\^\s*(\d+)");
        if (powMatch.Success)
        {
            if (!long.TryParse(powMatch.Groups[1].Value, out long baseNum) ||
                !long.TryParse(powMatch.Groups[2].Value, out long exponent))
                return "Invalid exponentiation operands.";

            long result = ModPow(baseNum, exponent, modulus);
            return $"{baseNum}^{exponent} mod {modulus} = {result}";
        }

        var opMatch = Regex.Match(exprPart, @"(\d+)\s*([\+\-\*])\s*(\d+)");
        if (opMatch.Success)
        {
            if (!long.TryParse(opMatch.Groups[1].Value, out long a) ||
                !long.TryParse(opMatch.Groups[3].Value, out long b))
                return "Invalid operands.";

            char op = opMatch.Groups[2].Value[0];
            long result = op switch
            {
                '+' => (a + b) % modulus,
                '-' => ((a - b) % modulus + modulus) % modulus,
                '*' => (a * b) % modulus,
                _ => 0
            };

            return $"{a} {op} {b} mod {modulus} = {result}";
        }

        return "Could not parse expression. Try formats like '7 + 5 mod 12', '15 - 8 mod 10', '3 * 4 mod 7', or '2^10 mod 13'.";
    }

    private long ModPow(long baseNum, long exponent, int modulus)
    {
        long result = 1;
        baseNum %= modulus;
        if (baseNum < 0) baseNum += modulus;

        while (exponent > 0)
        {
            if ((exponent & 1) == 1)
                result = (result * baseNum) % modulus;
            baseNum = (baseNum * baseNum) % modulus;
            exponent >>= 1;
        }

        return result;
    }
}