using System.Windows.Threading;
using Vinliste.Core.Config;
using Vinliste.Core.Data;
using Vinliste.Core.Http;
using Vinliste.Core.Ratings;
using Vinliste.Core.Sync;
using Vinliste.Core.Vinmonopolet;

namespace Vinliste.App.Services;

/// <summary>Everything the view models need, created once at startup.</summary>
public sealed class AppServices : IDisposable
{
    private readonly HttpClientFetcher _http;
    private readonly HttpClientFetcher _search;
    private readonly WebView2Fetcher _browser;

    public AppServices(Dispatcher dispatcher)
    {
        Settings = AppSettings.Load();
        // First start: begin from the bundled catalogue instead of an empty list.
        SeedInstalled = SeedDatabase.InstallIfNeeded(AppPaths.DatabasePath,
            () => typeof(AppServices).Assembly.GetManifestResourceStream("vinliste-seed.db.gz"));
        Repository = new WineRepository(new VinlisteDatabase(AppPaths.DatabasePath));
        _http = new HttpClientFetcher();
        // Search failures are recorded and retried next run; long retry loops would stall every source.
        _search = new HttpClientFetcher(maxAttempts: 2, delay: (_, ct) => Task.Delay(TimeSpan.FromSeconds(2), ct));
        _browser = new WebView2Fetcher(dispatcher, AppPaths.WebViewDataDirectory);
        Vinmonopolet = new VinmonopoletClient(_http, TimeSpan.FromMilliseconds(Math.Clamp(Settings.RequestDelayMs, 0, 5000)));
        Catalog = new CatalogSyncService(Repository, Vinmonopolet);
        Details = new DetailsSyncService(Repository, Vinmonopolet);
        Stock = new StockService(Repository, Vinmonopolet);
        Background = new BackgroundCatalogUpdater(Catalog, BackgroundPageDelay);
        RebuildRatings();
    }

    /// <summary>True when this start installed the bundled starter catalogue.</summary>
    public bool SeedInstalled { get; }

    public BackgroundCatalogUpdater Background { get; }

    public TimeSpan BackgroundPageDelay => TimeSpan.FromSeconds(Math.Clamp(Settings.BackgroundPageDelaySeconds, 10, 3600));

    public AppSettings Settings { get; }
    public WineRepository Repository { get; }
    public VinmonopoletClient Vinmonopolet { get; }
    public CatalogSyncService Catalog { get; }
    public DetailsSyncService Details { get; }
    public StockService Stock { get; }
    public RatingService Ratings { get; private set; } = null!;

    /// <summary>Recreates the rating service after the enabled sources or threshold change.</summary>
    public void RebuildRatings()
    {
        var sources = RatingSourceFactory.CreateAll(new RatingFetchers(_http, _browser, _search))
            .Where(s => Settings.EnabledRatingSources.Contains(s.Id))
            .ToList();
        Ratings = new RatingService(Repository, sources, Settings.MatchThreshold);
    }

    public void SaveSettings() => Settings.Save();

    /// <summary>Starts, stops or re-paces background updates to match the settings.</summary>
    public Task ApplyBackgroundSettingsAsync() => Background.RestartAsync(Settings.BackgroundUpdates, BackgroundPageDelay);

    public void Dispose()
    {
        // Each page is saved as it arrives, so stopping only cancels the wait for the next one.
        Background.StopAsync().Wait(TimeSpan.FromSeconds(5));
        _browser.Dispose();
        _search.Dispose();
        _http.Dispose();
    }
}
