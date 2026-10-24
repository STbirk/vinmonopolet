using System.Text.Json;
using Vinliste.Core.Data;
using Vinliste.Core.Http;
using Vinliste.Core.Sync;
using Vinliste.Core.Vinmonopolet;

namespace Vinliste.Core.Tests;

public class CatalogSyncServiceTests
{
    private static readonly IReadOnlyList<WineCategory> Categories = [new("rødvin", "Rødvin"), new("hvitvin", "Hvitvin")];

    private static string Page(int page, int totalPages, params string[] codes) => JsonSerializer.Serialize(new
    {
        pagination = new { currentPage = page, totalPages, totalResults = totalPages * 2 },
        products = codes.Select(code => new
        {
            code,
            name = $"Wine {code}",
            main_category = new { code = code.StartsWith('w') ? "hvitvin" : "rødvin", name = "x" },
            price = new { value = 100.0 },
            volume = new { value = 75.0 },
            buyable = true,
        }),
    });

    private static FakeFetcher Catalogue(Func<string, int, string> page) => new FakeFetcher()
        .On(url => url.EndsWith("vinmonopolet.no/"), _ => new FetchResponse(200, "<html/>"))
        .On(url => url.Contains("/search"), url =>
        {
            var category = Uri.UnescapeDataString(url).Contains("hvitvin") ? "hvitvin" : "rødvin";
            var current = int.Parse(url[(url.IndexOf("currentPage=", StringComparison.Ordinal) + 12)..]);
            return new FetchResponse(200, page(category, current));
        });

    [Fact]
    public async Task Full_run_pages_every_category_and_delists_unseen_wines()
    {
        using var db = new TempDatabase();
        db.Repository.UpsertListings([Wines.Listing("stale")], DateTimeOffset.UtcNow.AddDays(-1));
        var fetcher = Catalogue((category, current) => category == "rødvin"
            ? Page(current, 2, $"r{current}a", $"r{current}b")
            : Page(current, 1, "w0"));
        var service = new CatalogSyncService(db.Repository, new VinmonopoletClient(fetcher, TimeSpan.Zero));
        var reports = new List<SyncProgress>();

        var result = await service.RunAsync(Categories, new SyncProgressCollector(reports), CancellationToken.None);

        Assert.Equal(SyncRunStatus.Completed, result.Status);
        Assert.Equal(5, result.ProductsSeen);
        Assert.Equal(5, result.ProductsNew);
        // The first run never delists: a wine must be missing from two runs in a row.
        Assert.Equal(0, result.Delisted);
        Assert.Contains(reports, r => r.Stage == "Done");
        Assert.Null(db.Repository.GetResumableRun(CatalogSyncService.Kind));

        var second = await service.RunAsync(Categories, null, CancellationToken.None);

        Assert.Equal(0, second.ProductsNew);
        Assert.Equal(1, second.Delisted);
        Assert.Equal(5, db.Repository.Count(new WineQuery()));
        Assert.NotNull(db.Repository.GetWine("stale")!.DelistedAt);
    }

    [Fact]
    public async Task Empty_page_mid_category_does_not_delist_the_rest()
    {
        using var db = new TempDatabase();
        var glitch = false;
        var fetcher = Catalogue((category, current) =>
            glitch && current == 1 ? Page(1, 3) : Page(current, 3, $"r{current}a", $"r{current}b"));
        var service = new CatalogSyncService(db.Repository, new VinmonopoletClient(fetcher, TimeSpan.Zero));
        IReadOnlyList<WineCategory> red = [new("rødvin", "Rødvin")];
        await service.RunAsync(red, null, CancellationToken.None);
        await service.RunAsync(red, null, CancellationToken.None);

        glitch = true;
        var result = await service.RunAsync(red, null, CancellationToken.None);

        Assert.Equal(SyncRunStatus.Completed, result.Status);
        Assert.Equal(0, result.Delisted);
        Assert.Equal(6, db.Repository.Count(new WineQuery()));
    }

    [Fact]
    public async Task Unexpected_exception_marks_run_failed_instead_of_leaving_it_running()
    {
        using var db = new TempDatabase();
        var fetcher = new FakeFetcher()
            .On(url => url.EndsWith("vinmonopolet.no/"), _ => new FetchResponse(200, ""))
            .On(url => url.Contains("currentPage=1"), _ => throw new IOException("connection reset"))
            .On(url => url.Contains("/search"), _ => new FetchResponse(200, Page(0, 2, "r0")));
        var service = new CatalogSyncService(db.Repository, new VinmonopoletClient(fetcher, TimeSpan.Zero));

        var result = await service.RunAsync([new WineCategory("rødvin", "Rødvin")], null, CancellationToken.None);

        Assert.Equal(SyncRunStatus.Failed, result.Status);
        Assert.Equal(SyncRunStatus.Failed, db.Repository.GetLastRun(CatalogSyncService.Kind)!.Status);
    }

    [Fact]
    public async Task Cancelling_while_counting_returns_cancelled()
    {
        using var db = new TempDatabase();
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var service = new CatalogSyncService(db.Repository, new VinmonopoletClient(new FakeFetcher(), TimeSpan.Zero));

        var result = await service.RunAsync(Categories, null, cts.Token);

        Assert.Equal(SyncRunStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task Cancelled_run_resumes_from_the_next_page()
    {
        using var db = new TempDatabase();
        var cts = new CancellationTokenSource();
        var listingCalls = 0;
        var fetcher = new FakeFetcher()
            .On(url => url.EndsWith("vinmonopolet.no/"), _ => new FetchResponse(200, ""))
            .On(url => url.Contains("/search"), url =>
            {
                var current = int.Parse(url[(url.IndexOf("currentPage=", StringComparison.Ordinal) + 12)..]);
                var white = Uri.UnescapeDataString(url).Contains("hvitvin");
                // Counting pass fetches page 0 of both categories; cancel after the first real page.
                if (++listingCalls == 3)
                {
                    cts.Cancel();
                }
                return new FetchResponse(200, white ? Page(current, 1, "w0") : Page(current, 3, $"r{current}"));
            });
        var service = new CatalogSyncService(db.Repository, new VinmonopoletClient(fetcher, TimeSpan.Zero));

        var first = await service.RunAsync(Categories, null, cts.Token);
        Assert.Equal(SyncRunStatus.Cancelled, first.Status);
        Assert.Equal(1, db.Repository.Count(new WineQuery()));

        fetcher.Requests.Clear();
        var second = await service.RunAsync(Categories, null, CancellationToken.None);

        Assert.Equal(SyncRunStatus.Completed, second.Status);
        Assert.Equal(["r0", "r1", "r2", "w0"], db.Repository.Query(new WineQuery()).Select(w => w.Code).Order());
        // Resumed at red page 1: no new counting pass and red page 0 is not fetched again.
        Assert.DoesNotContain(fetcher.Requests, url => url.Contains("currentPage=0") && !Uri.UnescapeDataString(url).Contains("hvitvin"));
    }

    [Fact]
    public async Task Http_failure_marks_run_failed_and_keeps_it_resumable()
    {
        using var db = new TempDatabase();
        var fetcher = new FakeFetcher()
            .On(url => url.EndsWith("vinmonopolet.no/"), _ => new FetchResponse(200, ""))
            .On(url => url.Contains("currentPage=1"), _ => new FetchResponse(503, "down"))
            .On(url => url.Contains("/search"), _ => new FetchResponse(200, Page(0, 2, "r0")));
        var service = new CatalogSyncService(db.Repository, new VinmonopoletClient(fetcher, TimeSpan.Zero));

        var result = await service.RunAsync([new WineCategory("rødvin", "Rødvin")], null, CancellationToken.None);

        Assert.Equal(SyncRunStatus.Failed, result.Status);
        Assert.Contains("503", result.Error);
        Assert.NotNull(db.Repository.GetResumableRun(CatalogSyncService.Kind));
    }

    [Fact]
    public async Task Details_sync_fetches_missing_details_and_skips_404()
    {
        using var db = new TempDatabase();
        db.Repository.UpsertListings([Wines.Listing("11929001"), Wines.Listing("missing")], DateTimeOffset.UtcNow);
        var fetcher = new FakeFetcher()
            .On(url => url.EndsWith("vinmonopolet.no/"), _ => new FetchResponse(200, ""))
            .On("/products/11929001", Fixtures.Read("vmp_product_full.json"))
            .On("/products/missing", "{}", 404);
        var service = new DetailsSyncService(db.Repository, new VinmonopoletClient(fetcher, TimeSpan.Zero));

        var saved = await service.RunAsync(null, 10, null, CancellationToken.None);

        Assert.Equal(1, saved);
        Assert.NotNull(db.Repository.GetDetails("11929001"));
        Assert.Null(db.Repository.GetDetails("missing"));
        // The 404 is remembered so the next run does not start with it again.
        Assert.Empty(db.Repository.CodesNeedingDetails(DetailsSyncService.MaxAge, 10));
    }

    [Fact]
    public async Task Stock_follows_every_page()
    {
        using var db = new TempDatabase();
        db.Repository.UpsertListings([Wines.Listing("11929001")], DateTimeOffset.UtcNow);
        string StockPage(int page, int totalPages, string storeId) => JsonSerializer.Serialize(new
        {
            pagination = new { currentPage = page, totalPages },
            stores = new[] { new { pointOfService = new { id = storeId, displayName = $"Store {storeId}" }, stockInfo = new { stockLevel = 3 } } },
        });
        var fetcher = new FakeFetcher()
            .On(url => url.EndsWith("vinmonopolet.no/"), _ => new FetchResponse(200, ""))
            .On(url => url.Contains("/stock") && url.Contains("currentPage=0"), _ => new FetchResponse(200, StockPage(0, 2, "1")))
            .On(url => url.Contains("/stock") && url.Contains("currentPage=1"), _ => new FetchResponse(200, StockPage(1, 2, "2")));
        var service = new StockService(db.Repository, new VinmonopoletClient(fetcher, TimeSpan.Zero));

        var stock = await service.RefreshAsync("11929001", null, null, CancellationToken.None);

        Assert.Equal(["1", "2"], stock.Select(s => s.Store.StoreId));
    }

    private sealed class SyncProgressCollector(List<SyncProgress> reports) : IProgress<SyncProgress>
    {
        public void Report(SyncProgress value) => reports.Add(value);
    }
}
