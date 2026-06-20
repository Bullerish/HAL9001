// hal9001:name=reverse-this-sentence
// hal9001:request=reverse this sentence
using System;
using HAL9001;

public class ReverseHandler : IHandler
{
    public string Handle(string input)
    {
        string lowerInput = input.ToLower();
        
        if (lowerInput.Contains("reverse"))
        {
            int reverseIndex = lowerInput.IndexOf("reverse");
            int thisIndex = lowerInput.IndexOf("this");
            
            if (thisIndex >= 0)
            {
                int startIndex = thisIndex + 4;
                while (startIndex < input.Length && char.IsWhiteSpace(input[startIndex]))
                {
                    startIndex++;
                }
                
                string textToReverse = input.Substring(startIndex).Trim();
                
                if (!string.IsNullOrEmpty(textToReverse))
                {
                    char[] chars = textToReverse.ToCharArray();
                    Array.Reverse(chars);
                    return new string(chars);
                }
            }
        }
        
        return "Please provide text to reverse using the format 'reverse this [text]'";
    }
}