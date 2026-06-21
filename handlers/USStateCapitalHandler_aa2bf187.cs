// hal9001:name=get-us-state-capital
// hal9001:description=Retrieve the capital city of any US state by name
// hal9001:request=what is the capital of Missouri?
using System;
using System.Collections.Generic;
using HAL9001;

public class USStateCapitalHandler : IHandler
{
    private static readonly Dictionary<string, string> StateCapitals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "Alabama", "Montgomery" },
        { "Alaska", "Juneau" },
        { "Arizona", "Phoenix" },
        { "Arkansas", "Little Rock" },
        { "California", "Sacramento" },
        { "Colorado", "Denver" },
        { "Connecticut", "Hartford" },
        { "Delaware", "Dover" },
        { "Florida", "Tallahassee" },
        { "Georgia", "Atlanta" },
        { "Hawaii", "Honolulu" },
        { "Idaho", "Boise" },
        { "Illinois", "Springfield" },
        { "Indiana", "Indianapolis" },
        { "Iowa", "Des Moines" },
        { "Kansas", "Topeka" },
        { "Kentucky", "Frankfort" },
        { "Louisiana", "Baton Rouge" },
        { "Maine", "Augusta" },
        { "Maryland", "Annapolis" },
        { "Massachusetts", "Boston" },
        { "Michigan", "Lansing" },
        { "Minnesota", "Saint Paul" },
        { "Mississippi", "Jackson" },
        { "Missouri", "Jefferson City" },
        { "Montana", "Helena" },
        { "Nebraska", "Lincoln" },
        { "Nevada", "Carson City" },
        { "New Hampshire", "Concord" },
        { "New Jersey", "Trenton" },
        { "New Mexico", "Santa Fe" },
        { "New York", "Albany" },
        { "North Carolina", "Raleigh" },
        { "North Dakota", "Bismarck" },
        { "Ohio", "Columbus" },
        { "Oklahoma", "Oklahoma City" },
        { "Oregon", "Salem" },
        { "Pennsylvania", "Harrisburg" },
        { "Rhode Island", "Providence" },
        { "South Carolina", "Columbia" },
        { "South Dakota", "Pierre" },
        { "Tennessee", "Nashville" },
        { "Texas", "Austin" },
        { "Utah", "Salt Lake City" },
        { "Vermont", "Montpelier" },
        { "Virginia", "Richmond" },
        { "Washington", "Olympia" },
        { "West Virginia", "Charleston" },
        { "Wisconsin", "Madison" },
        { "Wyoming", "Cheyenne" }
    };

    public string Handle(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "Please provide a state name.";

        string stateName = ExtractStateName(input);

        if (string.IsNullOrEmpty(stateName))
            return "Could not identify a state name in your request.";

        if (StateCapitals.TryGetValue(stateName, out string capital))
            return $"The capital of {stateName} is {capital}.";

        return $"'{stateName}' is not a recognized US state. Please check the spelling and try again.";
    }

    private string ExtractStateName(string input)
    {
        input = input.ToLower();
        
        foreach (var state in StateCapitals.Keys)
        {
            if (input.Contains(state.ToLower()))
                return state;
        }

        return null;
    }
}