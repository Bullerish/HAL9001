// hal9001:name=did-you-want-me
// hal9001:request=Did you want me to reverse the characters or reverse the order of the words?
using System;
using HAL9001;

public class ReverseHandler : IHandler
{
    public string Handle(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "Please provide text to reverse.";
        
        var lowerInput = input.ToLower();
        
        if (lowerInput.Contains("reverse") && (lowerInput.Contains("character") || lowerInput.Contains("char")))
        {
            var chars = input.ToCharArray();
            Array.Reverse(chars);
            return new string(chars);
        }
        
        if (lowerInput.Contains("reverse") && lowerInput.Contains("word"))
        {
            var words = input.Split(new[] { ' ' }, StringSplitOptions.None);
            Array.Reverse(words);
            return string.Join(" ", words);
        }
        
        if (lowerInput.Contains("reverse"))
        {
            return "Did you want me to reverse the characters or reverse the order of the words?";
        }
        
        return "I can help you reverse text. Would you like to reverse the characters or the word order?";
    }
}