using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Vinliste.Core.Data;

namespace Vinliste.App.Services;

/// <summary>Writes the current wine list to CSV (Excel-friendly) or JSON.</summary>
public static class Exporter
{
    private static readonly string[] Headers =
    [
        "Code", "Name", "Producer", "Vintage", "Type", "Country", "District", "Price", "Litre price", "Volume (cl)",
        "Alcohol", "Best score", "Rating sources", "Available online", "Available in stores", "Assortment", "Favourite", "Note", "URL",
    ];

    public static void Write(string path, IReadOnlyList<WineRow> wines)
    {
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllText(path, JsonSerializer.Serialize(wines, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            return;
        }

        // Excel opens semicolon-separated UTF-8 with BOM correctly in Norwegian locales.
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(';', Headers));
        foreach (var w in wines)
        {
            builder.AppendLine(string.Join(';', new object?[]
            {
                w.Code, w.Name, w.Producer, w.Vintage, w.Category, w.Country, w.District, w.Price, w.LitrePrice, w.VolumeCl,
                w.Alcohol, w.BestScore, w.RatingSources, w.AvailableOnline, w.AvailableInStores, w.ProductSelection,
                w.IsFavorite, w.Note, w.Url,
            }.Select(Field)));
        }
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string Field(object? value)
    {
        var text = value switch
        {
            null => "",
            double d => d.ToString(CultureInfo.CurrentCulture),
            bool b => b ? "Yes" : "No",
            _ => value.ToString() ?? "",
        };
        // A text cell starting with = + - @ would run as a formula in Excel (notes are user text).
        if (value is string && text.Length > 0 && text[0] is '=' or '+' or '-' or '@')
        {
            text = "'" + text;
        }
        return text.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }
}
