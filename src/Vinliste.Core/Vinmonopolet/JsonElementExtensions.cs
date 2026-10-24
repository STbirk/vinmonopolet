using System.Globalization;
using System.Text.Json;

namespace Vinliste.Core.Vinmonopolet;

/// <summary>Null-tolerant accessors: Vinmonopolet omits or empties fields freely.</summary>
internal static class JsonElementExtensions
{
    public static JsonElement GetPropertyOrDefault(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    public static string? GetString(this JsonElement element, string name)
    {
        var value = element.GetPropertyOrDefault(name);
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    public static double? GetDouble(this JsonElement element, string name)
    {
        var value = element.GetPropertyOrDefault(name);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return number;
        }
        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString()?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
        return null;
    }

    public static int? GetInt(this JsonElement element, string name)
    {
        var number = element.GetDouble(name);
        return number is null ? null : (int)number.Value;
    }

    public static bool? GetBool(this JsonElement element, string name)
    {
        var value = element.GetPropertyOrDefault(name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    public static IEnumerable<JsonElement> GetArray(this JsonElement element, string name)
    {
        var value = element.GetPropertyOrDefault(name);
        return value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
    }
}
