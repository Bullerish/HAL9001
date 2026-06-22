// hal9001:name=factorial
// hal9001:description=Calculate the factorial of a non-negative integer
// hal9001:request=what is 9 factorial
// hal9001:intype=Int
// hal9001:outtype=Int
// hal9001:stability=Stable
using System;
using HAL9001;

public class FactorialHandler : IHandler
{
    public string Handle(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "Please provide a number to calculate the factorial of.";

        int number = ExtractInteger(input);
        
        if (number == -1)
            return "Could not find a valid non-negative integer in your request.";
        
        if (number < 0)
            return "Factorial is only defined for non-negative integers.";
        
        if (number > 20)
            return "Number is too large to compute factorial (max supported: 20).";
        
        long result = CalculateFactorial(number);
        return result.ToString();
    }
    
    private int ExtractInteger(string input)
    {
        string lowerInput = input.ToLower();
        
        // Remove common ordinal suffixes and words
        string cleaned = lowerInput
            .Replace("st", "").Replace("nd", "").Replace("rd", "").Replace("th", "")
            .Replace("factorial", "")
            .Replace("of", "")
            .Replace("the", "")
            .Replace("number", "")
            .Replace("what is", "")
            .Replace("calculate", "");
        
        // Try to find any number in the string
        string numberStr = "";
        foreach (char c in cleaned)
        {
            if (char.IsDigit(c))
                numberStr += c;
            else if (!string.IsNullOrEmpty(numberStr))
                break;
        }
        
        if (string.IsNullOrEmpty(numberStr))
            return -1;
        
        if (int.TryParse(numberStr, out int result))
            return result;
        
        return -1;
    }
    
    private long CalculateFactorial(int n)
    {
        if (n <= 1)
            return 1;
        
        long result = 1;
        for (int i = 2; i <= n; i++)
            result *= i;
        
        return result;
    }
}