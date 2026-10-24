using System.Globalization;
using Vinliste.Core.Http;
using Vinliste.Core.Models;

namespace Vinliste.Core.Vinmonopolet;

public sealed class VinmonopoletException(string message) : Exception(message);

/// <summary>Client for Vinmonopolet's public web JSON endpoints.</summary>
public sealed class VinmonopoletClient(IHttpFetcher fetcher, TimeSpan? requestDelay = null)
{
    private const string JsonAccept = "application/json, text/plain, */*";
    private readonly TimeSpan _requestDelay = requestDelay ?? TimeSpan.FromMilliseconds(300);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;
    private bool _warmedUp;

    public static string SearchUrl(string categoryCode, int page, string? storeId = null)
    {
        var query = $":name-asc:mainCategory:{categoryCode}";
        if (!string.IsNullOrEmpty(storeId))
        {
            query += $":availableInStores:{storeId}";
        }
        return $"{VinmonopoletParser.SiteBaseUrl}/vmpws/v2/vmp/products/search?searchType=product"
            + $"&q={Uri.EscapeDataString(query)}&pageSize={WineCategories.PageSize}&currentPage={page}";
    }

    public static string ProductUrl(string code) =>
        $"{VinmonopoletParser.SiteBaseUrl}/vmpws/v3/vmp/products/{Uri.EscapeDataString(code)}?fields=FULL";

    /// <summary>The server caps stock pages at 10 stores, whatever pageSize asks for.</summary>
    public static string StockUrl(string code, double latitude, double longitude, int page = 0) =>
        $"{VinmonopoletParser.SiteBaseUrl}/vmpws/v2/vmp/products/{Uri.EscapeDataString(code)}/stock"
        + $"?pageSize=10&currentPage={page}&fields=BASIC"
        + $"&latitude={latitude.ToString(CultureInfo.InvariantCulture)}&longitude={longitude.ToString(CultureInfo.InvariantCulture)}";

    public async Task<ListingPage> GetListingPageAsync(string categoryCode, int page, CancellationToken ct, string? storeId = null)
    {
        var body = await GetJsonAsync(SearchUrl(categoryCode, page, storeId), ct).ConfigureAwait(false);
        return VinmonopoletParser.ParseSearchPage(body);
    }

    public async Task<WineDetails?> GetDetailsAsync(string code, CancellationToken ct)
    {
        var response = await FetchAsync(ProductUrl(code), ct).ConfigureAwait(false);
        if (response.StatusCode is 404 or 400)
        {
            return null;
        }
        EnsureSuccess(response, code);
        return VinmonopoletParser.ParseProduct(response.Body);
    }

    /// <summary>Stock in every store that has the wine, nearest to the location first.</summary>
    public async Task<IReadOnlyList<StoreStock>> GetAllStockAsync(string code, double latitude, double longitude, CancellationToken ct, int maxPages = 40)
    {
        var all = new List<StoreStock>();
        for (var page = 0; page < maxPages; page++)
        {
            var body = await GetJsonAsync(StockUrl(code, latitude, longitude, page), ct).ConfigureAwait(false);
            var stock = VinmonopoletParser.ParseStock(body, DateTimeOffset.UtcNow);
            all.AddRange(stock.Where(s => all.All(existing => existing.Store.StoreId != s.Store.StoreId)));
            if (stock.Count == 0 || page + 1 >= VinmonopoletParser.ParseStockTotalPages(body))
            {
                break;
            }
        }
        return all;
    }

    private async Task<string> GetJsonAsync(string url, CancellationToken ct)
    {
        var response = await FetchAsync(url, ct).ConfigureAwait(false);
        EnsureSuccess(response, url);
        return response.Body;
    }

    private async Task<FetchResponse> FetchAsync(string url, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_warmedUp)
            {
                // The JSON endpoints expect the session cookies the home page sets.
                await fetcher.GetAsync(VinmonopoletParser.SiteBaseUrl + "/", "text/html", ct).ConfigureAwait(false);
                _warmedUp = true;
            }
            var wait = _lastRequest + _requestDelay - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            var response = await fetcher.GetAsync(url, JsonAccept, ct).ConfigureAwait(false);
            _lastRequest = DateTimeOffset.UtcNow;
            return response;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void EnsureSuccess(FetchResponse response, string what)
    {
        if (!response.IsSuccess)
        {
            var snippet = response.Body.Length > 200 ? response.Body[..200] : response.Body;
            throw new VinmonopoletException($"Vinmonopolet returned {response.StatusCode} for {what}: {snippet}");
        }
    }
}
