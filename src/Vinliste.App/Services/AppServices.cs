using System.Windows.Threading;
using Vinliste.Core.Config;
using Vinliste.Core.Data;
using Vinliste.Core.Diagnostics;
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
    private readonly IHttpFetcher _loggedHttp;
    private readonly IHttpFetcher _loggedSearch;
    private readonly IHttpFetcher _loggedBrowser;

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
        // Every request shows up in the activity panel (when "web requests" is ticked there).
        _loggedHttp = new LoggingFetcher(_http, Activity, "web");
        _loggedSearch = new LoggingFetcher(_search, Activity, "search");
        _loggedBrowser = new LoggingFetcher(_browser, Activity, "browser");

        Vinmonopolet = new VinmonopoletClient(_loggedHttp, TimeSpan.FromMilliseconds(Math.Clamp(Settings.RequestDelayMs, 0, 5000)));
        Catalog = new CatalogSyncService(Repository, Vinmonopolet);
        Details = new DetailsSyncService(Repository, Vinmonopolet);
        Stock = new StockService(Repository, Vinmonopolet);
        RebuildRatings();
        Background = new BackgroundCatalogUpdater(Catalog, BackgroundPageDelay, Activity);
        BackgroundRatings = new BackgroundRatingUpdater(Repository, () => Ratings, Details, BackgroundRatingDelay, Activity);
        if (SeedInstalled)
        {
            Activity.Write("app", "Installed the starter wine list", ActivityLevel.Success);
        }
    }

    /// <summary>The log behind the activity panel.</summary>
    public ActivityLog Activity { get; } = new();

    /// <summary>True when this start installed the bundled starter catalogue.</summary>
    public bool SeedInstalled { get; }

    public BackgroundCatalogUpdater Background { get; }

    public BackgroundRatingUpdater BackgroundRatings { get; }

    public TimeSpan BackgroundPageDelay => TimeSpan.FromSeconds(Math.Clamp(Settings.BackgroundPageDelaySeconds, 10, 3600));

    public TimeSpan BackgroundRatingDelay => TimeSpan.FromSeconds(Math.Clamp(Settings.BackgroundRatingDelaySeconds, 30, 3600));

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
        var sources = RatingSourceFactory.CreateAll(new RatingFetchers(_loggedHttp, _loggedBrowser, _loggedSearch))
            .Where(s => Settings.EnabledRatingSources.Contains(s.Id))
            .ToList();
        Ratings = new RatingService(Repository, sources, Settings.MatchThreshold);
    }

    public void SaveSettings() => Settings.Save();

    private readonly SemaphoreSlim _applyGate = new(1, 1);

    /// <summary>
    /// Starts, stops or re-paces both background loops to match the settings. Calls run one after
    /// another, so two quick saves (on, then off) always end in the state of the last one.
    /// </summary>
    public async Task ApplyBackgroundSettingsAsync()
    {
        await _applyGate.WaitAsync();
        try
        {
            await Task.WhenAll(
                Background.RestartAsync(Settings.BackgroundUpdates, BackgroundPageDelay),
                BackgroundRatings.RestartAsync(Settings.BackgroundRatings, BackgroundRatingDelay));
        }
        finally
        {
            _applyGate.Release();
        }
    }

    public void Dispose()
    {
        // Every step is saved as it completes, so stopping only cancels the wait for the next one.
        // Keep the dispatcher pumping while waiting: a Falstaff/WE page fetch finishes its
        // cancellation on the UI thread, and a plain Wait() here would block it for the full timeout.
        var stopped = Task.WhenAll(Background.StopAsync(), BackgroundRatings.StopAsync());
        var frame = new DispatcherFrame();
        stopped.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        var timeout = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Normal, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        if (!stopped.IsCompleted)
        {
            Dispatcher.PushFrame(frame);
        }
        timeout.Stop();
        _browser.Dispose();
        _search.Dispose();
        _http.Dispose();
    }
}
