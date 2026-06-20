using System;
using System.Collections.Generic;
using System.Linq;
using HAL9001;

public class VowelAnalysisHandler : IHandler
{
    public string Handle(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "No input provided.";

        string lowerInput = input.ToLower();
        char[] vowels = { 'a', 'e', 'i', 'o', 'u' };
        
        Dictionary<char, int> vowelCounts = new Dictionary<char, int>();
        foreach (char vowel in vowels)
        {
            vowelCounts[vowel] = 0;
        }

        foreach (char c in lowerInput)
        {
            if (vowelCounts.ContainsKey(c))
                vowelCounts[c]++;
        }

        var sortedVowels = vowelCounts.OrderByDescending(kvp => kvp.Value).ToList();
        
        if (sortedVowels.All(kvp => kvp.Value == 0))
            return "No vowels found in the input.";

        int maxCount = sortedVowels[0].Value;
        var mostFrequent = sortedVowels.Where(kvp => kvp.Value == maxCount).ToList();

        string vowelList = string.Join(", ", mostFrequent.Select(kvp => $"'{kvp.Key}' ({kvp.Value} times)"));
        
        return $"The most frequent vowel(s): {vowelList}";
    }
}