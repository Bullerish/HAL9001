// hal9001:name=city-river-proximity
// hal9001:description=Determine which major rivers are located near a given city
// hal9001:request=Is Jefferson City located near any major rivers?
using System;
using System.Collections.Generic;
using System.Linq;
using HAL9001;

public class CityRiverProximityHandler : IHandler
{
    private static readonly Dictionary<string, List<string>> CityRiverMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
    {
        { "Jefferson City", new List<string> { "Missouri River" } },
        { "Kansas City", new List<string> { "Missouri River", "Kansas River" } },
        { "St. Louis", new List<string> { "Mississippi River", "Missouri River" } },
        { "Memphis", new List<string> { "Mississippi River" } },
        { "New Orleans", new List<string> { "Mississippi River" } },
        { "Pittsburgh", new List<string> { "Allegheny River", "Monongahela River", "Ohio River" } },
        { "Cincinnati", new List<string> { "Ohio River" } },
        { "Louisville", new List<string> { "Ohio River" } },
        { "Nashville", new List<string> { "Cumberland River" } },
        { "Chicago", new List<string> { "Chicago River", "Lake Michigan" } },
        { "Detroit", new List<string> { "Detroit River", "Lake Michigan", "Lake Erie", "Lake Huron" } },
        { "Minneapolis", new List<string> { "Minnesota River", "Mississippi River" } },
        { "Denver", new List<string> { "South Platte River" } },
        { "Sacramento", new List<string> { "Sacramento River" } },
        { "San Antonio", new List<string> { "San Antonio River" } },
        { "Portland", new List<string> { "Willamette River", "Columbia River" } },
        { "Seattle", new List<string> { "Puget Sound", "Duwamish River" } },
        { "Phoenix", new List<string> { "Salt River" } },
        { "Houston", new List<string> { "Buffalo Bayou", "San Jacinto River" } },
        { "Dallas", new List<string> { "Trinity River" } },
        { "Austin", new List<string> { "Colorado River", "Brazos River" } },
        { "Boston", new List<string> { "Charles River", "Boston Harbor" } },
        { "New York", new List<string> { "Hudson River", "East River", "Harlem River" } },
        { "Philadelphia", new List<string> { "Delaware River", "Schuylkill River" } },
        { "Washington", new List<string> { "Potomac River", "Anacostia River" } },
        { "Atlanta", new List<string> { "Chattahoochee River" } },
        { "Miami", new List<string> { "Miami River", "Biscayne Bay" } },
        { "Tampa", new List<string> { "Hillsborough River", "Tampa Bay" } },
        { "New Orleans", new List<string> { "Mississippi River" } },
        { "Baton Rouge", new List<string> { "Mississippi River" } }
    };

    public string Handle(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "Please provide a city name to check for nearby rivers.";

        var cleanedInput = input.Trim();
        var cityName = ExtractCityName(cleanedInput);

        if (string.IsNullOrWhiteSpace(cityName))
            return "I could not identify a city name in your request.";

        if (CityRiverMap.TryGetValue(cityName, out var rivers))
        {
            if (rivers.Count == 0)
                return $"{cityName} does not have any major rivers located near it in my database.";

            var riverList = string.Join(", ", rivers);
            return $"Yes, {cityName} is located near the following major river(s): {riverList}.";
        }

        return $"I don't have river information for {cityName} in my database. Try another city.";
    }

    private string ExtractCityName(string input)
    {
        var lowerInput = input.ToLower();
        
        foreach (var city in CityRiverMap.Keys)
        {
            if (lowerInput.Contains(city.ToLower()))
                return city;
        }

        var words = input.Split(new[] { ' ', ',', '?', '.' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 0)
        {
            foreach (var word in words)
            {
                foreach (var city in CityRiverMap.Keys)
                {
                    if (word.Equals(city, StringComparison.OrdinalIgnoreCase))
                        return city;
                }
            }
        }

        return null;
    }
}