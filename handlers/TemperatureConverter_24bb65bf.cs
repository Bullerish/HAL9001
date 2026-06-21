// hal9001:name=temperature-converter
// hal9001:description=Convert temperature values between Fahrenheit, Celsius, and Kelvin scales.
// hal9001:request=convert 100 fahrenheit to celsius
using System;
using HAL9001;

public class TemperatureConverter : IHandler
{
    public string Handle(string input)
    {
        try
        {
            var normalized = input.ToLower().Trim();
            
            // Parse input: "convert X <from-unit> to <to-unit>"
            var parts = normalized.Split(new[] { " " }, StringSplitOptions.RemoveEmptyEntries);
            
            if (parts.Length < 4 || parts[0] != "convert")
                return "Invalid format. Use: convert <value> <from-unit> to <to-unit>";
            
            if (!double.TryParse(parts[1], out double value))
                return $"Could not parse '{parts[1]}' as a number.";
            
            var fromUnit = NormalizeUnit(parts[2]);
            
            // Find "to" keyword
            int toIndex = -1;
            for (int i = 3; i < parts.Length - 1; i++)
            {
                if (parts[i] == "to")
                {
                    toIndex = i;
                    break;
                }
            }
            
            if (toIndex == -1)
                return "Could not find 'to' in conversion request.";
            
            var toUnit = NormalizeUnit(string.Join(" ", parts, toIndex + 1, parts.Length - toIndex - 1));
            
            if (string.IsNullOrEmpty(fromUnit) || string.IsNullOrEmpty(toUnit))
                return "Invalid temperature units. Supported: Fahrenheit, Celsius, Kelvin";
            
            double result = ConvertTemperature(value, fromUnit, toUnit);
            
            return $"{value}° {GetDisplayName(fromUnit)} = {result:F2}° {GetDisplayName(toUnit)}";
        }
        catch (Exception ex)
        {
            return $"Error processing request: {ex.Message}";
        }
    }
    
    private string NormalizeUnit(string unit)
    {
        unit = unit.ToLower().Trim();
        
        if (unit.StartsWith("f") || unit == "fahrenheit")
            return "fahrenheit";
        if (unit.StartsWith("c") || unit == "celsius")
            return "celsius";
        if (unit.StartsWith("k") || unit == "kelvin")
            return "kelvin";
        
        return null;
    }
    
    private string GetDisplayName(string unit)
    {
        return unit switch
        {
            "fahrenheit" => "F",
            "celsius" => "C",
            "kelvin" => "K",
            _ => unit
        };
    }
    
    private double ConvertTemperature(double value, string fromUnit, string toUnit)
    {
        if (fromUnit == toUnit)
            return value;
        
        // Convert to Celsius first as intermediate
        double celsius = fromUnit switch
        {
            "fahrenheit" => (value - 32) * 5 / 9,
            "celsius" => value,
            "kelvin" => value - 273.15,
            _ => throw new InvalidOperationException($"Unknown unit: {fromUnit}")
        };
        
        // Convert from Celsius to target unit
        return toUnit switch
        {
            "fahrenheit" => celsius * 9 / 5 + 32,
            "celsius" => celsius,
            "kelvin" => celsius + 273.15,
            _ => throw new InvalidOperationException($"Unknown unit: {toUnit}")
        };
    }
}