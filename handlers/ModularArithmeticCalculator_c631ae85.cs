// hal9001:name=modular-arithmetic-calculator
// hal9001:description=Compute modular arithmetic operations (addition, subtraction, multiplication, exponentiation) with a given modulus
// hal9001:request=modular arithmetic is neat
// hal9001:intype=String
// hal9001:outtype=String
// hal9001:stability=Stable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HAL9001;

public class ModularArithmeticCalculator : IHandler
{
    public string Handle(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "Please provide a modular arithmetic expression (e.g., '5 + 3 mod 7' or '2^10 mod 13').";

        try
        {
            input = input.ToLower().Trim();
            
            // Extract modulus first
            var modMatch = Regex.Match(input, @"mod\s+(\d+)");
            if (!modMatch.Success)
                return "No modulus found. Use 'mod N' to specify the modulus (e.g., '5 + 3 mod 7').";
            
            long modulus = long.Parse(modMatch.Groups[1].Value);
            if (modulus <= 0)
                return "Modulus must be a positive integer.";
            
            // Remove the 'mod N' part to extract the expression
            string expression = Regex.Replace(input, @"mod\s+\d+", "").Trim();
            
            if (string.IsNullOrWhiteSpace(expression))
                return "No expression provided. Use format: 'A op B mod N' (e.g., '5 + 3 mod 7').";
            
            // Tokenize the expression
            var tokens = Regex.Split(expression, @"(?<=[+\-*^])|(?=[+\-*^])")
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();
            
            if (tokens.Count == 0)
                return "Could not parse the expression.";
            
            // Parse and evaluate
            long result = EvaluateExpression(tokens, modulus);
            
            // Normalize result to [0, modulus)
            result = ((result % modulus) + modulus) % modulus;
            
            return $"{result}";
        }
        catch (OverflowException)
        {
            return "Numbers too large for calculation.";
        }
        catch (DivideByZeroException)
        {
            return "Cannot divide by zero.";
        }
        catch (Exception ex)
        {
            return $"Error parsing expression: {ex.Message}";
        }
    }
    
    private long EvaluateExpression(List<string> tokens, long modulus)
    {
        // Handle exponentiation first (highest precedence)
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i] == "^")
            {
                if (i == 0 || i == tokens.Count - 1)
                    throw new ArgumentException("Invalid exponentiation syntax.");
                
                long baseNum = long.Parse(tokens[i - 1]);
                long exponent = long.Parse(tokens[i + 1]);
                
                long powResult = ModularExponentiation(baseNum, exponent, modulus);
                
                tokens.RemoveRange(i - 1, 3);
                tokens.Insert(i - 1, powResult.ToString());
                i--;
            }
        }
        
        // Handle multiplication (second precedence)
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i] == "*")
            {
                if (i == 0 || i == tokens.Count - 1)
                    throw new ArgumentException("Invalid multiplication syntax.");
                
                long left = long.Parse(tokens[i - 1]);
                long right = long.Parse(tokens[i + 1]);
                long result = (left * right) % modulus;
                
                tokens.RemoveRange(i - 1, 3);
                tokens.Insert(i - 1, result.ToString());
                i--;
            }
        }
        
        // Handle addition and subtraction (left to right)
        long accumulator = long.Parse(tokens[0]);
        for (int i = 1; i < tokens.Count; i += 2)
        {
            if (i + 1 >= tokens.Count)
                throw new ArgumentException("Invalid expression syntax.");
            
            string op = tokens[i];
            long operand = long.Parse(tokens[i + 1]);
            
            if (op == "+")
                accumulator = (accumulator + operand) % modulus;
            else if (op == "-")
                accumulator = ((accumulator - operand) % modulus + modulus) % modulus;
            else
                throw new ArgumentException($"Unexpected operator: {op}");
        }
        
        return accumulator;
    }
    
    private long ModularExponentiation(long baseNum, long exponent, long modulus)
    {
        if (exponent < 0)
            throw new ArgumentException("Negative exponents not supported in modular arithmetic.");
        
        long result = 1;
        baseNum = baseNum % modulus;
        
        while (exponent > 0)
        {
            if ((exponent & 1) == 1)
                result = (result * baseNum) % modulus;
            
            exponent >>= 1;
            baseNum = (baseNum * baseNum) % modulus;
        }
        
        return result;
    }
}