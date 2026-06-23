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

        // Pattern to match expressions like: number operator number mod modulus
        // Also support: (number operator number) mod modulus
        var pattern = @"(\(?)(\d+)\s*([+\-*/^])\s*(\d+)(\)?)?\s*mod\s+(\d+)";
        var match = Regex.Match(input, pattern);

        if (!match.Success)
        {
            // Check if the input is just asking about modular arithmetic conceptually
            if (input.Contains("modular arithmetic") || input.Contains("mod"))
                return "Modular arithmetic computes remainders. Provide an expression like '5 + 3 mod 7' or '2^10 mod 13'.";
            
            return "No valid modular arithmetic expression found. Use format: 'a operator b mod m' (operators: +, -, *, /, ^).";
        }

        if (!long.TryParse(match.Groups[2].Value, out long num1))
            return "Invalid first number.";

        if (!long.TryParse(match.Groups[4].Value, out long num2))
            return "Invalid second number.";

        if (!long.TryParse(match.Groups[6].Value, out long modulus))
            return "Invalid modulus.";

        if (modulus <= 0)
            return "Modulus must be positive.";

        string op = match.Groups[3].Value;

        try
        {
            long result = 0;

            switch (op)
            {
                case "+":
                    result = ((num1 % modulus) + (num2 % modulus)) % modulus;
                    break;
                case "-":
                    result = ((num1 % modulus) - (num2 % modulus) + modulus) % modulus;
                    break;
                case "*":
                    result = ((num1 % modulus) * (num2 % modulus)) % modulus;
                    break;
                case "/":
                    if (num2 == 0)
                        return "Division by zero is undefined.";
                    // For modular division, we'd need modular inverse; not implementing full support
                    return "Modular division requires modular inverse (not supported). Try +, -, *, or ^.";
                case "^":
                    result = ModPow(num1, num2, modulus);
                    break;
                default:
                    return "Unsupported operator.";
            }

            return $"{num1} {op} {num2} ≡ {result} (mod {modulus})";
        }
        catch (OverflowException)
        {
            return "Computation overflow. Numbers or exponent too large.";
        }
        catch (Exception ex)
        {
            return $"Error during computation: {ex.Message}";
        }
    }

    private long ModPow(long baseNum, long exponent, long modulus)
    {
        if (exponent < 0)
            throw new ArgumentException("Negative exponents not supported in modular arithmetic.");

        long result = 1;
        baseNum %= modulus;

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