using System.IO.Compression;
using System.Text.Json;
using Vinliste.Core.Data;
using Vinliste.Core.Http;
using Vinliste.Core.Sync;
using Vinliste.Core.Vinmonopolet;

namespace Vinliste.Core.Tests;

public class BackgroundUpdateTests
{
    private static string Page(int page, int totalPages, string category, params string[] codes) => JsonSerializer.Serialize(new
    {
        pagination = new { currentPage = page, totalPages, totalResults = totalPages },
        products = codes.Select(code => new
        {
            code,
            name = $"Wine {code}",
            main_category = new { code = category, name = category },
            price = new { value = 100.0 },
        }),
    });

    /// <summary>One page per category; counts listing requests.</summary>
    private static FakeFetcher OnePagePerCategory(Action? onListing = null) => new FakeFetcher()
        .On(url => url.EndsWith("vinmonopolet.no/"), _ => new FetchResponse(200, ""))
        .On(url => url.Contains("/search"), url =>
        {
            onListing?.Invoke();
            var category = WineCategories.All.First(c => Uri.UnescapeDataString(url).Contains($":mainCategory:{c.Code}")).Code;
            return new FetchResponse(200, Page(0, 1, category, $"{category}-1"));
        });

    [Fact]
    public async Task Slow_run_waits_before_each_page()
    {
        using var db = new TempDatabase();
        var service = new CatalogSyncService(db.Repository, new VinmonopoletClient(OnePagePerCategory(), TimeSpan.Zero));
        IReadOnlyList<WineCategory> two = WineCategories.All.Take(2).ToList();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var result = await service.RunAsync(two, null, CancellationToken.None, new CatalogSyncOptions(TimeSpan.FromMilliseconds(150), TimeSpan.FromDays(7)));

        Assert.Equal(SyncRunStatus.Completed, result.Status);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(280), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task Background_updater_completes_passes_and_stops_cleanly()
    {
        using var db = new TempDatabase();
        var service = new CatalogSyncService(db.Repository, new VinmonopoletClient(OnePagePerCategory(), TimeSpan.Zero));
        var updater = new BackgroundCatalogUpdater(service, TimeSpan.FromMilliseconds(5));
        var passes = 0;
        var twoPasses = new TaskCompletionSource();
        updater.PassCompleted += (_, _) =>
        {
            if (Interlocked.Increment(ref passes) == 2)
            {
                twoPasses.TrySetResult();
            }
        };

        updater.Start();
        await twoPasses.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await updater.StopAsync();

        Assert.False(updater.IsRunning);
        Assert.Equal(WineCategories.All.Count, db.Repository.Count(new WineQuery()));
        Assert.False(updater.Status.IsActive);
    }

    [Fact]
    public async Task Stopped_slow_pass_is_resumed_by_a_fast_update()
    {
        using var db = new TempDatabase();
        var listings = 0;
        var service = new CatalogSyncService(db.Repository, new VinmonopoletClient(OnePagePerCategory(() => Interlocked.Increment(ref listings)), TimeSpan.Zero));
        var updater = new BackgroundCatalogUpdater(service, TimeSpan.FromSeconds(30));

        updater.Start();
        // Counting fetches page 0 of every category, then the slow pass waits 30 s before the first page.
        while (Volatile.Read(ref listings) < WineCategories.All.Count)
        {
            await Task.Delay(20);
        }
        await updater.StopAsync();
        var run = db.Repository.GetLastRun(CatalogSyncService.Kind)!;
        Assert.Equal(SyncRunStatus.Cancelled, run.Status);

        // The user's fast update continues the same run instead of counting again.
        var result = await service.RunAsync(null, null, CancellationToken.None);

        Assert.Equal(SyncRunStatus.Completed, result.Status);
        Assert.Equal(run.Id, db.Repository.GetLastRun(CatalogSyncService.Kind)!.Id);
        Assert.Equal(WineCategories.All.Count * 2, listings);
    }

    [Fact]
    public void Seed_installs_only_when_there_is_no_catalogue()
    {
        using var source = new TempDatabase();
        source.Repository.UpsertListings([Wines.Listing("1"), Wines.Listing("2")], DateTimeOffset.UtcNow);
        var seedPath = Path.Combine(Path.GetTempPath(), "vinliste-tests", Guid.NewGuid().ToString("N"), "seed.db.gz");
        SeedDatabase.Export(source.Database, seedPath);

        using var target = new TempDatabase();
        var targetPath = target.Database.Path;

        // An empty (migrated) database gets the seed …
        Assert.True(SeedDatabase.InstallIfNeeded(targetPath, () => File.OpenRead(seedPath)));
        Assert.Equal(2, new WineRepository(new VinlisteDatabase(targetPath)).Count(new WineQuery()));

        // … a database with wines is never overwritten.
        Assert.False(SeedDatabase.InstallIfNeeded(targetPath, () => File.OpenRead(seedPath)));

        // No seed bundled: nothing happens.
        var missing = Path.Combine(Path.GetDirectoryName(seedPath)!, "fresh", "vinliste.db");
        Assert.False(SeedDatabase.InstallIfNeeded(missing, () => null));
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public async Task Restart_applies_a_new_pace_and_off_then_on_stays_on()
    {
        using var db = new TempDatabase();
        var service = new CatalogSyncService(db.Repository, new VinmonopoletClient(OnePagePerCategory(), TimeSpan.Zero));
        var updater = new BackgroundCatalogUpdater(service, TimeSpan.FromSeconds(30));
        updater.Start();

        // Off and straight back on (two quick saves in Settings) leaves it running …
        _ = updater.RestartAsync(false, TimeSpan.FromSeconds(30));
        await updater.RestartAsync(true, TimeSpan.FromMilliseconds(5));
        Assert.True(updater.IsRunning);

        // … with the new pace: a pass that would take minutes at 30 s per page finishes now.
        var done = new TaskCompletionSource();
        updater.PassCompleted += (_, _) => done.TrySetResult();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await updater.StopAsync();
    }

    [Fact]
    public async Task Slow_pass_with_recent_progress_resumes_even_when_started_long_ago()
    {
        using var db = new TempDatabase();
        var listings = 0;
        var service = new CatalogSyncService(db.Repository, new VinmonopoletClient(OnePagePerCategory(() => Interlocked.Increment(ref listings)), TimeSpan.Zero));
        var cts = new CancellationTokenSource();
        // Cancel after the counting pass and the first page.
        var progress = new SyncProgressCallback(p =>
        {
            if (p.Stage == "Updating")
            {
                cts.Cancel();
            }
        });
        await service.RunAsync(null, progress, cts.Token);
        var run = db.Repository.GetLastRun(CatalogSyncService.Kind)!;
        // The pass started 30 days ago, but its last page was saved just now.
        using (var connection = db.Database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE sync_runs SET started_at = $s WHERE id = $id";
            command.Parameters.AddWithValue("$s", DateTimeOffset.UtcNow.AddDays(-30).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"));
            command.Parameters.AddWithValue("$id", run.Id);
            command.ExecuteNonQuery();
        }
        var before = listings;

        var result = await service.RunAsync(null, null, CancellationToken.None);

        Assert.Equal(SyncRunStatus.Completed, result.Status);
        Assert.Equal(run.Id, db.Repository.GetLastRun(CatalogSyncService.Kind)!.Id);
        // Resumed: no second counting pass, only the remaining pages.
        Assert.Equal(WineCategories.All.Count - 1, listings - before);
    }

    [Fact]
    public void Corrupt_database_is_never_replaced_by_the_seed()
    {
        using var source = new TempDatabase();
        source.Repository.UpsertListings([Wines.Listing("1")], DateTimeOffset.UtcNow);
        var dir = Path.Combine(Path.GetTempPath(), "vinliste-tests", Guid.NewGuid().ToString("N"));
        var seedPath = Path.Combine(dir, "seed.db.gz");
        SeedDatabase.Export(source.Database, seedPath);
        var corrupt = Path.Combine(dir, "vinliste.db");
        File.WriteAllText(corrupt, "this is not a database, but it may be the user's damaged one");

        Assert.False(SeedDatabase.InstallIfNeeded(corrupt, () => File.OpenRead(seedPath)));
        Assert.StartsWith("this is not", File.ReadAllText(corrupt));

        // A zero-byte file holds nothing and does get the seed.
        File.WriteAllBytes(corrupt, []);
        Assert.True(SeedDatabase.InstallIfNeeded(corrupt, () => File.OpenRead(seedPath)));
    }

    private sealed class SyncProgressCallback(Action<SyncProgress> callback) : IProgress<SyncProgress>
    {
        public void Report(SyncProgress value) => callback(value);
    }

    [Fact]
    public void Exported_seed_is_a_plain_gzipped_sqlite_file()
    {
        using var source = new TempDatabase();
        source.Repository.UpsertListings([Wines.Listing("1")], DateTimeOffset.UtcNow);
        var seedPath = Path.Combine(Path.GetTempPath(), "vinliste-tests", Guid.NewGuid().ToString("N"), "seed.db.gz");

        SeedDatabase.Export(source.Database, seedPath);

        using var gzip = new GZipStream(File.OpenRead(seedPath), CompressionMode.Decompress);
        var header = new byte[16];
        gzip.ReadExactly(header);
        Assert.Equal("SQLite format 3\0", System.Text.Encoding.ASCII.GetString(header));
    }
}
