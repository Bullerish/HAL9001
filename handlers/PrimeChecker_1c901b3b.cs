// hal9001:name=check-prime-number
// hal9001:description=Determine whether a given integer is prime
// hal9001:request=is 91 prime?
using System;
using HAL9001;

public class PrimeChecker : IHandler
{
    public string Handle(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "Please provide a number to check.";

        // Extract the number from the input
        string cleaned = System.Text.RegularExpressions.Regex.Replace(input, @"[^\d-]", " ");
        string[] tokens = cleaned.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0)
            return "Could not find a number in your input.";

        if (!long.TryParse(tokens[0], out long number))
            return "Could not parse a valid integer from your input.";

        bool isPrime = IsPrime(number);
        return $"{number} is {(isPrime ? "prime" : "not prime")}.";
    }

    private bool IsPrime(long number)
    {
        if (number < 2)
            return false;
        if (number == 2)
            return true;
        if (number % 2 == 0)
            return false;

        long sqrtNumber = (long)Math.Sqrt(number);
        for (long i = 3; i <= sqrtNumber; i += 2)
        {
            if (number % i == 0)
                return false;
        }

        return true;
    }
}