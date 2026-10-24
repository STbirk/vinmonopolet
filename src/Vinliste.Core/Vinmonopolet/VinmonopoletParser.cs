using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vinliste.Core.Models;

namespace Vinliste.Core.Vinmonopolet;

/// <summary>Turns Vinmonopolet web JSON (search, product, stock) into models.</summary>
public static partial class VinmonopoletParser
{
    public const string SiteBaseUrl = "https://www.vinmonopolet.no";

    public static ListingPage ParseSearchPage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var pagination = root.GetPropertyOrDefault("pagination");
        var wines = new List<WineListing>();
        if (root.TryGetProperty("products", out var products) && products.ValueKind == JsonValueKind.Array)
        {
            foreach (var product in products.EnumerateArray())
            {
                var wine = ParseListing(product);
                if (wine is not null)
                {
                    wines.Add(wine);
                }
            }
        }
        return new ListingPage(
            wines,
            pagination.GetInt("currentPage") ?? 0,
            pagination.GetInt("totalPages") ?? 0,
            pagination.GetInt("totalResults") ?? wines.Count);
    }

    public static WineListing? ParseListing(JsonElement product)
    {
        var code = product.GetString("code");
        var name = product.GetString("name");
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var category = product.GetPropertyOrDefault("main_category");
        var volumeCl = product.GetPropertyOrDefault("volume").GetDouble("value");
        var price = product.GetPropertyOrDefault("price").GetDouble("value");
        var availability = product.GetPropertyOrDefault("productAvailability");
        var delivery = availability.GetPropertyOrDefault("deliveryAvailability");
        var stores = availability.GetPropertyOrDefault("storesAvailability");

        return new WineListing
        {
            Code = code,
            Name = name.Trim(),
            CategoryCode = category.GetString("code"),
            Category = category.GetString("name"),
            SubCategory = product.GetPropertyOrDefault("main_sub_category").GetString("name"),
            Country = product.GetPropertyOrDefault("main_country").GetString("name"),
            District = product.GetPropertyOrDefault("district").GetString("name"),
            SubDistrict = product.GetPropertyOrDefault("sub_District").GetString("name"),
            Vintage = ExtractVintage(name),
            VolumeCl = volumeCl,
            Alcohol = product.GetPropertyOrDefault("alcohol").GetDouble("value"),
            Price = price,
            LitrePrice = price is not null && volumeCl is > 0 ? Math.Round(price.Value / (volumeCl.Value / 100), 2) : null,
            Status = product.GetString("status"),
            ProductSelection = product.GetString("product_selection"),
            Buyable = product.GetBool("buyable") ?? false,
            Expired = product.GetBool("expired") ?? false,
            AvailableOnline = delivery.GetBool("availableForPurchase") ?? false,
            AvailableInStores = stores.GetBool("availableForPurchase") ?? false,
            AvailabilityText = AvailabilitySummary(delivery, stores),
            Sustainable = product.GetBool("sustainable") ?? false,
            Url = ProductPageUrl(code),
            ImageUrl = PickImage(product, "product", "thumbnail"),
        };
    }

    public static WineDetails ParseProduct(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var p = doc.RootElement;
        var code = p.GetString("code") ?? throw new FormatException("Product JSON has no code");
        var content = p.GetPropertyOrDefault("content");

        var characteristics = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in content.GetArray("characteristics"))
        {
            var name = item.GetString("name");
            if (name is not null)
            {
                characteristics[name] = int.TryParse(item.GetString("value"), out var v) ? v : null;
            }
        }

        var traits = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in content.GetArray("traits"))
        {
            var name = item.GetString("name");
            if (name is not null)
            {
                traits[name] = item.GetString("formattedValue");
            }
        }

        var grapes = content.GetArray("ingredients")
            .Select(item => new Grape(
                item.GetString("name") ?? item.GetString("formattedValue") ?? "",
                ParsePercent(item.GetString("formattedValue"))))
            .Where(g => g.Name.Length > 0)
            .ToList();

        var food = content.GetArray("isGoodFor")
            .Select(item => new FoodPairing(item.GetString("code") ?? "", item.GetString("name") ?? ""))
            .Where(f => f.Name.Length > 0)
            .ToList();

        var style = content.GetPropertyOrDefault("style");

        return new WineDetails
        {
            Code = code,
            Vintage = NullIfEmpty(p.GetString("year")) ?? ExtractVintage(p.GetString("name")),
            LitrePrice = p.GetPropertyOrDefault("litrePrice").GetDouble("value"),
            Producer = p.GetPropertyOrDefault("main_producer").GetString("name"),
            Wholesaler = p.GetPropertyOrDefault("main_wholesaler").GetString("name"),
            Distributor = NullIfEmpty(p.GetString("distributor")),
            Colour = NullIfEmpty(p.GetString("color")),
            Smell = NullIfEmpty(p.GetString("smell")),
            Taste = NullIfEmpty(p.GetString("taste")),
            Method = NullIfEmpty(p.GetString("method")),
            Summary = NullIfEmpty(p.GetString("summary")) ?? NullIfEmpty(p.GetString("description")),
            StoragePotential = content.GetPropertyOrDefault("storagePotential").GetString("formattedValue"),
            StyleName = style.GetString("name"),
            StyleDescription = style.GetString("description"),
            Cork = NullIfEmpty(p.GetString("cork")),
            PackageType = NullIfEmpty(p.GetString("packageType")),
            Allergens = NullIfEmpty(p.GetString("allergens")),
            Sugar = traits.GetValueOrDefault("Sukker"),
            Acid = traits.GetValueOrDefault("Syre"),
            Fullness = characteristics.GetValueOrDefault("Fylde"),
            Freshness = characteristics.GetValueOrDefault("Friskhet"),
            Tannins = characteristics.GetValueOrDefault("Garvestoffer"),
            Sweetness = characteristics.GetValueOrDefault("Sødme"),
            Bitterness = characteristics.GetValueOrDefault("Bitterhet"),
            Eco = p.GetBool("eco") ?? false,
            Vegan = p.GetBool("vegan") ?? false,
            Biodynamic = p.GetBool("bioDynamic") ?? false,
            FairTrade = p.GetBool("fairTrade") ?? false,
            Kosher = p.GetBool("kosher") ?? false,
            Gluten = p.GetBool("gluten") ?? false,
            EnvironmentalPackaging = p.GetBool("environmentalPackaging") ?? false,
            ImageUrl = PickImage(p, "zoom", "product", "superZoom"),
            Grapes = grapes,
            FoodPairings = food,
            RawJson = json,
        };
    }

    public static IReadOnlyList<StoreStock> ParseStock(string json, DateTimeOffset observedAt)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new List<StoreStock>();
        foreach (var entry in doc.RootElement.GetArray("stores"))
        {
            var pos = entry.GetPropertyOrDefault("pointOfService");
            var id = pos.GetString("id") ?? pos.GetString("name");
            if (id is null)
            {
                continue;
            }
            var address = pos.GetPropertyOrDefault("address");
            var geo = pos.GetPropertyOrDefault("geoPoint");
            var store = new Store
            {
                StoreId = id,
                Name = pos.GetString("displayName") ?? id,
                Address = address.GetString("line1"),
                Town = address.GetString("town"),
                PostalCode = address.GetString("postalCode"),
                Phone = address.GetString("phone"),
                Latitude = geo.GetDouble("latitude"),
                Longitude = geo.GetDouble("longitude"),
            };
            var level = entry.GetPropertyOrDefault("stockInfo").GetInt("stockLevel") ?? 0;
            result.Add(new StoreStock(store, level, pos.GetString("formattedDistance"), observedAt));
        }
        return result;
    }

    public static int ParseStockTotalPages(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetPropertyOrDefault("pagination").GetInt("totalPages") ?? 1;
    }

    public static string? ExtractVintage(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }
        var match = VintageRegex().Match(value);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? AvailabilitySummary(JsonElement delivery, JsonElement stores)
    {
        var parts = new List<string>();
        foreach (var section in new[] { delivery, stores })
        {
            foreach (var info in section.GetArray("infos"))
            {
                var text = info.GetString("readableValue") ?? info.GetString("availability");
                if (!string.IsNullOrWhiteSpace(text) && !parts.Contains(text))
                {
                    parts.Add(text);
                }
            }
        }
        return parts.Count > 0 ? string.Join("; ", parts) : null;
    }

    /// <summary>
    /// The short product URL. Listing URLs vary (some stop at the district), but
    /// /p/{code} always redirects to the product page.
    /// </summary>
    public static string ProductPageUrl(string code) => $"{SiteBaseUrl}/p/{Uri.EscapeDataString(code)}";

    private static string? PickImage(JsonElement product, params string[] formats)
    {
        var images = product.GetArray("images").ToList();
        foreach (var format in formats)
        {
            var url = images.FirstOrDefault(i => i.GetString("format") == format).GetString("url");
            if (url is not null)
            {
                return url;
            }
        }
        return images.Select(i => i.GetString("url")).FirstOrDefault(u => u is not null);
    }

    private static double? ParsePercent(string? formatted)
    {
        if (formatted is null)
        {
            return null;
        }
        var match = PercentRegex().Match(formatted);
        return match.Success && double.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"\b(19\d{2}|20\d{2})\b")]
    private static partial Regex VintageRegex();

    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*%")]
    private static partial Regex PercentRegex();
}
