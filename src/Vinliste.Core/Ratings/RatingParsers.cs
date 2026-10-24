using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

namespace Vinliste.Core.Ratings;

/// <summary>Parsers for rating-site pages and search snippets.</summary>
public static partial class RatingParsers
{
    private static readonly string[] BlockMarkers =
    [
        "verify you are human", "access denied", "temporarily blocked", "cf-chl-", "challenge-platform",
        "just a moment...", "attention required", "captcha",
    ];

    /// <summary>True for bot challenges and block pages (CellarTracker serves an empty 202).</summary>
    public static bool IsBlocked(int statusCode, string body)
    {
        if (statusCode is 202 or 401 or 403 or 429)
        {
            return true;
        }
        var head = body.Length > 12000 ? body[..12000] : body;
        return BlockMarkers.Any(m => head.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Vivino wine/vintage page: the server-rendered props carry the rating per vintage.</summary>
    public static IReadOnlyList<RatingCandidate> ParseVivinoPage(string html, string url)
    {
        var document = new HtmlParser().ParseDocument(html);
        var candidates = new List<RatingCandidate>();
        foreach (var id in new[] { "ssr-wine-page-top-section", "ssr-vintage-page-lower-section" })
        {
            var raw = document.GetElementById(id)?.GetAttribute("data-ssr-props");
            if (string.IsNullOrEmpty(raw))
            {
                continue;
            }
            using var props = JsonDocument.Parse(raw);
            var root = props.RootElement;
            var vintage = root.TryGetProperty("vintage", out var v) ? v
                : root.TryGetProperty("vintagePageInformation", out var info) && info.TryGetProperty("vintage", out var v2) ? v2
                : default;
            if (vintage.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            var stats = Prop(vintage, "statistics");
            var wine = Prop(vintage, "wine");
            var producer = Str(Prop(wine, "winery"), "name");
            var score = Number(stats, "ratings_average");
            var year = Str(vintage, "year");
            var vintageName = Str(vintage, "name");
            candidates.Add(new RatingCandidate
            {
                Name = vintageName ?? Str(wine, "name") ?? "",
                Producer = producer,
                // Vivino uses "N.V." for non-vintage wines; keep it distinct from "any vintage".
                Vintage = WineText.ExtractVintage(year) ?? WineText.ExtractVintage(vintageName)
                    ?? (WineText.IsNonVintage(year) || WineText.IsNonVintage(vintageName) ? "NV" : null),
                Score = score is > 0 ? score : null,
                RatingCount = (int?)Number(stats, "ratings_count"),
                Url = url,
            });

            // The all-vintages rating: a fallback when this vintage has too few ratings for a score.
            var wineStats = Prop(wine, "statistics");
            var wineScore = Number(wineStats, "ratings_average");
            if (wineScore is > 0 && Str(wine, "name") is { } wineName)
            {
                candidates.Add(new RatingCandidate
                {
                    Name = producer is null ? wineName : $"{producer} {wineName}",
                    Producer = producer,
                    Vintage = null,
                    Score = wineScore,
                    RatingCount = (int?)Number(wineStats, "ratings_count"),
                    Url = url,
                });
            }
        }
        return candidates.Where(c => c.Name.Length > 0).DistinctBy(c => (c.Name, c.Vintage, c.Score)).ToList();
    }

    /// <summary>schema.org Review/AggregateRating blocks (Falstaff and Wine Enthusiast wine pages).</summary>
    public static IReadOnlyList<RatingCandidate> ParseJsonLd(string html, string url)
    {
        var document = new HtmlParser().ParseDocument(html);
        var fallbackName = document.QuerySelector("h1")?.TextContent.Trim();
        var candidates = new List<RatingCandidate>();
        foreach (var script in document.QuerySelectorAll("script[type='application/ld+json']"))
        {
            try
            {
                using var json = JsonDocument.Parse(script.TextContent);
                Walk(json.RootElement, null, null);
            }
            catch (JsonException)
            {
            }
        }
        return candidates.DistinctBy(c => (c.Name, c.Score)).ToList();

        void Walk(JsonElement value, string? inheritedName, string? inheritedBrand)
        {
            if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    Walk(item, inheritedName, inheritedBrand);
                }
                return;
            }
            if (value.ValueKind != JsonValueKind.Object)
            {
                return;
            }
            var isReview = TypeIs(value, "Review");
            var name = isReview && inheritedName is not null ? inheritedName : Str(value, "name") ?? inheritedName;
            var brand = Prop(value, "brand") is { ValueKind: JsonValueKind.Object } b ? Str(b, "name") : Str(value, "brand");
            brand ??= inheritedBrand;
            if (Prop(value, "itemReviewed") is { ValueKind: JsonValueKind.Object } reviewed && Str(reviewed, "name") is { } reviewedName)
            {
                name = reviewedName;
            }
            foreach (var key in new[] { "reviewRating", "aggregateRating" })
            {
                var rating = Prop(value, key);
                var score = Number(rating, "ratingValue");
                if (score is null)
                {
                    continue;
                }
                var best = Number(rating, "bestRating");
                // Only 100-point (or unstated) scales: a 5-star user rating is not a critic score.
                if (best is not null and < 50)
                {
                    continue;
                }
                var candidateName = name ?? fallbackName;
                if (string.IsNullOrWhiteSpace(candidateName))
                {
                    continue;
                }
                candidates.Add(new RatingCandidate
                {
                    Name = candidateName.Trim(),
                    Producer = brand,
                    Vintage = WineText.ExtractVintage(candidateName) ?? WineText.ExtractVintage(url),
                    Score = score,
                    RatingCount = (int?)(Number(rating, "ratingCount") ?? Number(rating, "reviewCount")),
                    Url = url,
                });
            }
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name is "reviewRating" or "aggregateRating")
                {
                    continue;
                }
                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    Walk(property.Value, name, brand);
                }
            }
        }
    }

    /// <summary>"Average of 88.4 points in 17 community wine reviews on 2001 Château … , plus …"</summary>
    public static RatingCandidate? ParseCellarTrackerSnippet(SearchResult result)
    {
        var match = CellarTrackerRegex().Match(result.Snippet);
        if (!match.Success)
        {
            return null;
        }
        var name = match.Groups["name"].Value.Trim();
        // Producer pages summarise many wines: "… on 66 wines from Château X".
        if (ProducerSummaryRegex().IsMatch(name))
        {
            return null;
        }
        return new RatingCandidate
        {
            Name = name,
            Vintage = WineText.ExtractVintage(name),
            Score = ParseNumber(match.Groups["score"].Value),
            RatingCount = (int?)ParseNumber(match.Groups["count"].Value.Replace(",", "")),
            Url = result.Url,
        };
    }

    /// <summary>
    /// A CellarTracker wine page whose snippet has no community average yet: still the right
    /// wine, so it is kept (without a score) to show the link instead of "not found".
    /// </summary>
    public static RatingCandidate? ParseCellarTrackerTitle(SearchResult result)
    {
        var title = CellarTrackerTitleSuffixRegex().Replace(result.Title, "").Trim();
        if (title.Length == 0 || title.EndsWith("...", StringComparison.Ordinal) || title.EndsWith('…')
            || !result.Url.Contains("wine.asp", StringComparison.OrdinalIgnoreCase)
            || title.StartsWith("Community Tasting", StringComparison.OrdinalIgnoreCase)
            || title.StartsWith("Article", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return new RatingCandidate
        {
            Name = title,
            Vintage = WineText.ExtractVintage(title) ?? (WineText.IsNonVintage(title) ? "NV" : null),
            Url = result.Url,
        };
    }

    /// <summary>"… vintage 2021 from the winery Caparzo has been rated … with 94 Falstaff points."</summary>
    public static RatingCandidate? ParseFalstaffSnippet(SearchResult result)
    {
        var text = result.Title + " " + result.Snippet;
        var score = FalstaffScoreRegex().Match(text);
        if (!score.Success)
        {
            return null;
        }
        var title = FalstaffTitleSuffixRegex().Replace(result.Title, "").Trim();
        var producer = FalstaffWineryRegex().Match(result.Snippet) is { Success: true } winery ? winery.Groups[1].Value.Trim() : null;
        var parts = title.Split(" - ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2)
        {
            producer ??= parts[^1];
            title = string.Join(" - ", parts[..^1]);
        }
        return new RatingCandidate
        {
            Name = title,
            Producer = producer,
            Vintage = WineText.ExtractVintage(title) ?? WineText.ExtractVintage(result.Url),
            Score = ParseNumber(score.Groups[1].Value),
            Url = result.Url,
        };
    }

    /// <summary>Wine Enthusiast buying-guide snippets: "… Rating 92 Price $60 Winery Franco Conterno …".</summary>
    public static RatingCandidate? ParseWineEnthusiastSnippet(SearchResult result)
    {
        var score = WineEnthusiastScoreRegex().Match(result.Snippet);
        if (!score.Success)
        {
            return null;
        }
        var title = WineEnthusiastTitleSuffixRegex().Replace(result.Title, "").Trim();
        var producer = WineEnthusiastWineryRegex().Match(result.Snippet) is { Success: true } winery ? winery.Groups[1].Value.Trim() : null;
        return new RatingCandidate
        {
            Name = title,
            Producer = producer,
            Vintage = WineText.ExtractVintage(title) ?? WineText.ExtractVintage(result.Url),
            Score = ParseNumber(score.Groups[1].Value),
            Url = result.Url,
        };
    }

    private static JsonElement Prop(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static string? Str(JsonElement element, string name)
    {
        var value = Prop(element, name);
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static double? Number(JsonElement element, string name)
    {
        var value = Prop(element, name);
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String => ParseNumber(value.GetString()),
            _ => null,
        };
    }

    private static bool TypeIs(JsonElement element, string type)
    {
        var value = Prop(element, "@type");
        return value.ValueKind switch
        {
            JsonValueKind.String => string.Equals(value.GetString(), type, StringComparison.OrdinalIgnoreCase),
            JsonValueKind.Array => value.EnumerateArray().Any(v => string.Equals(v.GetString(), type, StringComparison.OrdinalIgnoreCase)),
            _ => false,
        };
    }

    private static double? ParseNumber(string? value) =>
        double.TryParse(value?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;

    // The name can contain periods ("Puisseguin-St. Émilion"), so it only ends at ", plus",
    // a truncation ellipsis, or a period that ends the snippet.
    [GeneratedRegex(@"Average of\s+(?<score>\d+(?:[.,]\d+)?)\s+points\s+in\s+(?<count>[\d,]+)\s+community wine reviews?\s+on\s+(?<name>.+?)(?:\s*,\s+plus\b|\s*(?:\.\.\.|…)|\.?\s*$)", RegexOptions.IgnoreCase)]
    private static partial Regex CellarTrackerRegex();

    [GeneratedRegex(@"^\d+\s+wines\s+from\b", RegexOptions.IgnoreCase)]
    private static partial Regex ProducerSummaryRegex();

    // "94+ Falstaff points" is common for young wines.
    [GeneratedRegex(@"\b(\d{2,3}(?:[.,]\d+)?)\+?\s+Falstaff\s+points?\b", RegexOptions.IgnoreCase)]
    private static partial Regex FalstaffScoreRegex();

    [GeneratedRegex(@"\s+[-|]\s+Falstaff\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex FalstaffTitleSuffixRegex();

    [GeneratedRegex(@"from the winery\s+(.+?)\s+has been rated", RegexOptions.IgnoreCase)]
    private static partial Regex FalstaffWineryRegex();

    [GeneratedRegex(@"\bRating\s+(\d{2,3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex WineEnthusiastScoreRegex();

    // Also strips a truncated "Rating and Re…" tail that search engines leave on long titles.
    [GeneratedRegex(@"\s*(?:Rating(?:\s+and(?:\s+Re\w*)?)?\s*(?:\.\.\.|…)?|(?:\.\.\.|…))?\s*(?:[-|]\s*Wine Enthusiast)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex WineEnthusiastTitleSuffixRegex();

    [GeneratedRegex(@"\s+[-|]\s+CellarTracker\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex CellarTrackerTitleSuffixRegex();

    [GeneratedRegex(@"\bWinery\s+(.+?)\s+(?:Variety|Region|Designation|Wine Type)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WineEnthusiastWineryRegex();
}
