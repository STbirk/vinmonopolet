using System.Text.RegularExpressions;
using Vinliste.Core.Http;

namespace Vinliste.Core.Ratings;

public sealed record SourceLookup(IReadOnlyList<RatingCandidate> Candidates, string Query, bool Blocked = false, string? Error = null);

public interface IRatingSource
{
    /// <summary>Stable id stored in the database ("vivino", "falstaff" …).</summary>
    string Id { get; }

    string Label { get; }

    /// <summary>Converts the site's native score to 0–100.</summary>
    double ToScore100(double score);

    Task<SourceLookup> LookupAsync(WineIdentity wine, double threshold, CancellationToken ct);
}

/// <summary>
/// Network access for rating sources: plain HTTP where it works, a real browser engine where
/// sites block it, and a search fetcher that gives up quickly (search failures are retried
/// on the next run rather than stalling every source behind long retry loops).
/// </summary>
public sealed record RatingFetchers(IHttpFetcher Plain, IHttpFetcher Browser, IHttpFetcher? Search = null);

/// <summary>
/// Finds the wine's page through web search restricted to the site, reads scores from search
/// snippets where the site puts them there, and opens up to a few result pages otherwise. A
/// second, differently phrased search only runs when the first finds no scored match.
/// </summary>
public abstract partial class WebSearchRatingSource(WebSearch search) : IRatingSource
{
    protected const string HtmlAccept = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";
    private const int MaxPageFetches = 3;

    public abstract string Id { get; }

    public abstract string Label { get; }

    protected abstract string Domain { get; }

    /// <summary>Whether results for another vintage can be skipped (not for Vivino, whose pages cover every vintage).</summary>
    protected virtual bool VintageSpecificPages => true;

    public virtual double ToScore100(double score) => score <= 5 ? score * 20 : score;

    protected abstract IEnumerable<string> Queries(string name, string? vintage);

    /// <summary>Candidate from the search snippet alone, or null to open the page.</summary>
    protected virtual RatingCandidate? FromSnippet(SearchResult result) => null;

    /// <summary>Opens a result page and reads candidates; null means the page was blocked.</summary>
    protected virtual Task<IReadOnlyList<RatingCandidate>?> FromPageAsync(SearchResult result, WineIdentity wine, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RatingCandidate>?>([]);

    protected virtual bool IsWinePage(string url) => true;

    /// <summary>Key that identifies the same wine page under different URLs (locale paths, query strings).</summary>
    protected virtual string PageKey(string url) => url;

    public async Task<SourceLookup> LookupAsync(WineIdentity wine, double threshold, CancellationToken ct)
    {
        var name = WineText.AsciiQuery(WineText.StripVintage(WineText.StripVolume(WineText.ExpandAbbreviations(wine.FullName))));
        var vintage = wine.Vintage ?? WineText.ExtractVintage(wine.Name);
        var candidates = new List<RatingCandidate>();
        var seenPages = new HashSet<string>();
        var queries = Queries(name, vintage).Select(WineText.CollapseSpaces).Distinct().ToList();
        var pageFetches = 0;
        var blocked = false;
        var failedSearches = 0;

        foreach (var query in queries)
        {
            var results = await search.SearchAsync(query, Domain, ct).ConfigureAwait(false);
            if (results is null)
            {
                failedSearches++;
                continue;
            }
            foreach (var result in results.Where(r => IsWinePage(r.Url)))
            {
                if (!seenPages.Add(PageKey(result.Url)))
                {
                    continue;
                }
                // Snippets are free: read them all.
                var fromSnippet = FromSnippet(result);
                if (fromSnippet is not null)
                {
                    candidates.Add(fromSnippet);
                    continue;
                }
                if (pageFetches >= MaxPageFetches || !LooksRelevant(wine, vintage, result))
                {
                    continue;
                }
                pageFetches++;
                var fromPage = await FromPageAsync(result, wine, ct).ConfigureAwait(false);
                if (fromPage is null)
                {
                    blocked = true;
                    continue;
                }
                candidates.AddRange(fromPage);
            }
            if (WineMatcher.BestMatch(wine, candidates, threshold) is { Candidate.Score: not null })
            {
                break;
            }
        }

        var query0 = queries.FirstOrDefault() ?? name;
        var matched = WineMatcher.BestMatch(wine, candidates, threshold) is not null;
        if (!matched && failedSearches == queries.Count)
        {
            return new SourceLookup(candidates, query0, Error: "Web search failed; will retry later");
        }
        // A relevant page that was blocked means "unknown", not "no rating", even if some
        // unrelated snippet produced a candidate.
        return new SourceLookup(candidates, query0, Blocked: blocked && !matched);
    }

    /// <summary>Skip opening pages whose title names another producer or (for per-vintage sites) another vintage.</summary>
    private bool LooksRelevant(WineIdentity wine, string? vintage, SearchResult result)
    {
        var producer = WineMatcher.ProducerTokens(wine);
        var text = WineText.Tokens(result.Title + " " + result.Url.Replace('-', ' ').Replace('/', ' ')).ToHashSet();
        if (producer.Count > 0 && !producer.Any(text.Contains))
        {
            return false;
        }
        if (VintageSpecificPages && vintage is not null)
        {
            var years = YearRegex().Matches(result.Title + " " + result.Url).Select(m => m.Value).Distinct().ToList();
            if (years.Count > 0 && !years.Contains(vintage))
            {
                return false;
            }
        }
        return true;
    }

    protected static async Task<IReadOnlyList<RatingCandidate>?> FetchPageAsync(
        IHttpFetcher fetcher, string url, Func<string, string, IReadOnlyList<RatingCandidate>> parse, CancellationToken ct)
    {
        var response = await fetcher.GetAsync(url, HtmlAccept, ct).ConfigureAwait(false);
        if (RatingParsers.IsBlocked(response.StatusCode, response.Body))
        {
            return null;
        }
        return response.IsSuccess ? parse(response.Body, url) : [];
    }

    [GeneratedRegex(@"\b(?:19|20)\d{2}\b")]
    private static partial Regex YearRegex();
}

public sealed partial class VivinoSource(WebSearch search, RatingFetchers fetchers) : WebSearchRatingSource(search)
{
    public override string Id => "vivino";
    public override string Label => "Vivino";
    protected override string Domain => "vivino.com";
    protected override bool VintageSpecificPages => false;

    public override double ToScore100(double score) => score * 20;

    protected override IEnumerable<string> Queries(string name, string? vintage) =>
        [$"site:vivino.com {name}", $"{name} vivino"];

    protected override bool IsWinePage(string url) => WineIdRegex().IsMatch(url);

    // The same wine appears under /en/, /fr/, /US/en/ …; one fetch per wine id.
    protected override string PageKey(string url) => WineIdRegex().Match(url) is { Success: true } m ? m.Groups[1].Value : url;

    protected override Task<IReadOnlyList<RatingCandidate>?> FromPageAsync(SearchResult result, WineIdentity wine, CancellationToken ct) =>
        FetchPageAsync(fetchers.Plain, VintageUrl(result.Url, wine.Vintage ?? WineText.ExtractVintage(wine.Name)), RatingParsers.ParseVivinoPage, ct);

    /// <summary>Points a Vivino wine URL at the wanted vintage so its own rating is returned.</summary>
    public static string VintageUrl(string url, string? vintage)
    {
        var uri = new Uri(url);
        var path = uri.GetLeftPart(UriPartial.Path);
        return vintage is null ? path : $"{path}?year={vintage}";
    }

    [GeneratedRegex(@"/w/(\d+)")]
    private static partial Regex WineIdRegex();
}

public sealed class CellarTrackerSource(WebSearch search) : WebSearchRatingSource(search)
{
    public override string Id => "cellartracker";
    public override string Label => "CellarTracker";
    protected override string Domain => "cellartracker.com";

    // Yahoo returns nothing for a path-restricted site: here.
    protected override IEnumerable<string> Queries(string name, string? vintage) =>
        [$"site:cellartracker.com {vintage} {name}", $"{vintage} {name} CellarTracker"];

    protected override bool IsWinePage(string url) => url.Contains("wine.asp", StringComparison.OrdinalIgnoreCase);

    protected override string PageKey(string url) =>
        System.Text.RegularExpressions.Regex.Match(url, @"iWine=(\d+)", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : url;

    // CellarTracker pages sit behind a bot challenge; the search snippet carries the community average.
    protected override RatingCandidate? FromSnippet(SearchResult result) =>
        RatingParsers.ParseCellarTrackerSnippet(result) ?? RatingParsers.ParseCellarTrackerTitle(result);
}

public sealed class FalstaffSource(WebSearch search, RatingFetchers fetchers) : WebSearchRatingSource(search)
{
    public override string Id => "falstaff";
    public override string Label => "Falstaff";
    protected override string Domain => "falstaff.com";

    protected override IEnumerable<string> Queries(string name, string? vintage) =>
        [$"site:falstaff.com/en/wines {name} {vintage}", $"{name} {vintage} Falstaff"];

    protected override bool IsWinePage(string url) => url.Contains("/wines/", StringComparison.OrdinalIgnoreCase);

    protected override RatingCandidate? FromSnippet(SearchResult result) => RatingParsers.ParseFalstaffSnippet(result);

    protected override Task<IReadOnlyList<RatingCandidate>?> FromPageAsync(SearchResult result, WineIdentity wine, CancellationToken ct) =>
        FetchPageAsync(fetchers.Browser, result.Url, RatingParsers.ParseJsonLd, ct);
}

public sealed class WineEnthusiastSource(WebSearch search, RatingFetchers fetchers) : WebSearchRatingSource(search)
{
    public override string Id => "wine_enthusiast";
    public override string Label => "Wine Enthusiast";
    protected override string Domain => "wineenthusiast.com";

    protected override IEnumerable<string> Queries(string name, string? vintage) =>
        [$"site:wineenthusiast.com/buying-guide {name} {vintage}", $"{name} {vintage} Wine Enthusiast rating"];

    protected override bool IsWinePage(string url) => url.Contains("/buying-guide/", StringComparison.OrdinalIgnoreCase);

    protected override RatingCandidate? FromSnippet(SearchResult result) => RatingParsers.ParseWineEnthusiastSnippet(result);

    protected override Task<IReadOnlyList<RatingCandidate>?> FromPageAsync(SearchResult result, WineIdentity wine, CancellationToken ct) =>
        FetchPageAsync(fetchers.Browser, result.Url, RatingParsers.ParseJsonLd, ct);
}

public static class RatingSourceFactory
{
    public static IReadOnlyList<IRatingSource> CreateAll(RatingFetchers fetchers)
    {
        var search = new WebSearch(fetchers.Search ?? fetchers.Plain);
        return
        [
            new VivinoSource(search, fetchers),
            new CellarTrackerSource(search),
            new FalstaffSource(search, fetchers),
            new WineEnthusiastSource(search, fetchers),
        ];
    }
}
