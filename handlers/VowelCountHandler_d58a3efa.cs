using System;
using HAL9001;

public class VowelCountHandler : IHandler
{
    public string Handle(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "Please provide text to analyze.";

        string lowerInput = input.ToLower();
        int vowelCount = 0;

        foreach (char c in lowerInput)
        {
            if (c == 'a' || c == 'e' || c == 'i' || c == 'o' || c == 'u')
                vowelCount++;
        }

        return $"The text contains {vowelCount} vowels.";
    }
}