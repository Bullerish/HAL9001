// hal9001:name=prime-factorization
// hal9001:description=Factorize a given integer into its prime factors
// hal9001:request=i am obsessed with prime factorization
// hal9001:intype=Int
// hal9001:outtype=String
// hal9001:stability=Stable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HAL9001;

public class PrimeFactorization : IHandler
{
    public string Handle(string input)
    {
        int number = ExtractInteger(input);
        
        if (number == int.MinValue)
        {
            return "I couldn't find a valid integer in your request. Please provide a number to factorize.";
        }
        
        if (number < 2)
        {
            return $"The number {number} cannot be factorized into prime factors. Prime factorization requires integers >= 2.";
        }
        
        List<int> factors = GetPrimeFactors(number);
        string factorString = string.Join(" × ", factors);
        
        return $"The prime factorization of {number} is: {factorString}";
    }
    
    private int ExtractInteger(string input)
    {
        // Try to find a plain integer first
        Match numberMatch = Regex.Match(input, @"\b(\d+)\b");
        if (numberMatch.Success && int.TryParse(numberMatch.Groups[1].Value, out int num))
        {
            return num;
        }
        
        // Try ordinal patterns like "7th", "21st", "103rd"
        Match ordinalMatch = Regex.Match(input, @"\b(\d+)(?:st|nd|rd|th)\b", RegexOptions.IgnoreCase);
        if (ordinalMatch.Success && int.TryParse(ordinalMatch.Groups[1].Value, out int ordNum))
        {
            return ordNum;
        }
        
        return int.MinValue;
    }
    
    private List<int> GetPrimeFactors(int number)
    {
        List<int> factors = new List<int>();
        
        // Divide by 2 until odd
        while (number % 2 == 0)
        {
            factors.Add(2);
            number /= 2;
        }
        
        // Check odd divisors from 3 onwards
        for (int i = 3; i * i <= number; i += 2)
        {
            while (number % i == 0)
            {
                factors.Add(i);
                number /= i;
            }
        }
        
        // If number is still greater than 1, it's a prime factor itself
        if (number > 1)
        {
            factors.Add(number);
        }
        
        return factors;
    }
}