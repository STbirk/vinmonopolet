using System.Diagnostics;
using Vinliste.Core.Diagnostics;

namespace Vinliste.Core.Http;

/// <summary>Writes every request and its outcome to the activity log ("GET host/path → 200, 312 ms").</summary>
public sealed class LoggingFetcher(IHttpFetcher inner, ActivityLog log, string source) : IHttpFetcher
{
    public async Task<FetchResponse> GetAsync(string url, string accept, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var response = await inner.GetAsync(url, accept, cancellationToken).ConfigureAwait(false);
        // Successful requests are detail lines; refusals, blocks and failures stand out.
        var level = response.IsSuccess && response.StatusCode != 202 ? ActivityLevel.Detail : ActivityLevel.Warning;
        var outcome = response.StatusCode == 0 ? "failed" : response.StatusCode.ToString();
        log.Write(source, $"GET {Shorten(url)} → {outcome}, {clock.ElapsedMilliseconds:N0} ms", level);
        return response;
    }

    private static string Shorten(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }
        var text = uri.Host.Replace("www.", "") + Uri.UnescapeDataString(uri.PathAndQuery);
        return text.Length > 110 ? text[..110] + "…" : text;
    }
}
