using System.Text;
using System.Web;
using AngleSharp.Html.Parser;
using Vinliste.Core.Http;

namespace Vinliste.Core.Ratings;

public sealed record SearchResult(string Title, string Url, string Snippet);

/// <summary>
/// Web search used to find a wine's page on each rating site. Yahoo first (its
/// results carry useful snippets), Bing as fallback. Results are restricted to the
/// rating site's domain.
/// </summary>
public sealed class WebSearch(IHttpFetcher fetcher, TimeSpan? minInterval = null)
{
    private const string HtmlAccept = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";

    // All rating sources share the search engine, so the politeness limit is global.
    private readonly TimeSpan _minInterval = minInterval ?? TimeSpan.FromMilliseconds(1200);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _last = DateTimeOffset.MinValue;

    /// <summary>
    /// Results on <paramref name="domain"/>, or null when the search engine failed (so the caller
    /// records an error to retry soon, not a "no match" that sticks for a month).
    /// </summary>
    /// <remarks>
    /// Yahoo only: Bing returned no on-domain results for automated requests in testing, and
    /// Yahoo fails some queries deterministically with HTTP 500.
    /// </remarks>
    public async Task<IReadOnlyList<SearchResult>?> SearchAsync(string query, string domain, CancellationToken ct)
    {
        var yahoo = await ThrottledGetAsync($"https://search.yahoo.com/search?p={Uri.EscapeDataString(query)}", ct).ConfigureAwait(false);
        return yahoo.IsSuccess && IsResultsPage(yahoo.Body) ? ParseYahoo(yahoo.Body, domain) : null;
    }

    /// <summary>
    /// True for a real results page (with or without hits). A consent wall or error page served
    /// with 200 lacks the results container and counts as a failure, not as "no results".
    /// Do not test for consent hosts: every normal page links to guce.yahoo.com in its footer.
    /// </summary>
    public static bool IsResultsPage(string html) =>
        html.Contains("id=\"results\"", StringComparison.Ordinal)
        || html.Contains("searchCenterMiddle", StringComparison.Ordinal);

    private async Task<FetchResponse> ThrottledGetAsync(string url, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = _last + _minInterval - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            var response = await fetcher.GetAsync(url, HtmlAccept, ct).ConfigureAwait(false);
            _last = DateTimeOffset.UtcNow;
            return response;
        }
        finally
        {
            _gate.Release();
        }
    }

    public static IReadOnlyList<SearchResult> ParseYahoo(string html, string domain)
    {
        var document = new HtmlParser().ParseDocument(html);
        var results = new List<SearchResult>();
        foreach (var result in document.QuerySelectorAll("div.algo"))
        {
            var link = result.QuerySelector(".compTitle a[href]");
            if (link is null)
            {
                continue;
            }
            var url = UnwrapYahoo(link.GetAttribute("href") ?? "");
            if (!IsOnDomain(url, domain))
            {
                continue;
            }
            // The link also wraps the site name and a URL breadcrumb; the page title is the <h3>.
            var title = (link.QuerySelector("h3") ?? link).TextContent;
            var snippet = result.QuerySelector(".compText")?.TextContent ?? "";
            Add(results, title, url, snippet);
        }
        return results;
    }

    public static IReadOnlyList<SearchResult> ParseBing(string html, string domain)
    {
        var document = new HtmlParser().ParseDocument(html);
        var results = new List<SearchResult>();
        foreach (var result in document.QuerySelectorAll("li.b_algo"))
        {
            var link = result.QuerySelector("h2 a[href]");
            if (link is null)
            {
                continue;
            }
            var url = UnwrapBing(link.GetAttribute("href") ?? "");
            if (!IsOnDomain(url, domain))
            {
                continue;
            }
            var snippet = result.QuerySelector(".b_caption p, p.b_lineclamp2, p.b_lineclamp3, p.b_lineclamp4, p")?.TextContent ?? "";
            Add(results, link.TextContent, url, snippet);
        }
        return results;
    }

    private static void Add(List<SearchResult> results, string title, string url, string snippet)
    {
        if (results.Any(r => r.Url == url))
        {
            return;
        }
        results.Add(new SearchResult(WineText.CollapseSpaces(title), url, WineText.CollapseSpaces(snippet)));
    }

    public static bool IsOnDomain(string url, string domain) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));

    public static string UnwrapYahoo(string url)
    {
        const string marker = "/RU=";
        var index = url.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return url;
        }
        var encoded = url[(index + marker.Length)..];
        var end = encoded.IndexOf('/');
        return Uri.UnescapeDataString(end >= 0 ? encoded[..end] : encoded);
    }

    /// <summary>Bing wraps result links as /ck/a?…&amp;u=a1{base64url}.</summary>
    public static string UnwrapBing(string url)
    {
        if (!url.Contains("bing.com/ck/a", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }
        var query = HttpUtility.ParseQueryString(new Uri(url).Query);
        var wrapped = query["u"];
        if (wrapped is null || !wrapped.StartsWith("a1", StringComparison.Ordinal))
        {
            return url;
        }
        var base64 = wrapped[2..].Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            return url;
        }
    }
}
