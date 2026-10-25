using Vinliste.Core.Data;
using Vinliste.Core.Diagnostics;
using Vinliste.Core.Http;
using Vinliste.Core.Models;
using Vinliste.Core.Ratings;
using Vinliste.Core.Sync;
using Vinliste.Core.Vinmonopolet;

namespace Vinliste.Core.Tests;

public class BackgroundRatingTests
{
    private sealed class CountingSource(string id, Func<WineIdentity, SourceLookup> lookup) : IRatingSource
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public string Id => id;
        public string Label => id;
        public double ToScore100(double score) => score <= 5 ? score * 20 : score;

        public Task<SourceLookup> LookupAsync(WineIdentity wine, double threshold, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(lookup(wine));
        }
    }

    private static DetailsSyncService NoDetails(WineRepository repository) =>
        new(repository, new VinmonopoletClient(new FakeFetcher(), TimeSpan.Zero));

    [Fact]
    public void Next_wine_prefers_favourites_then_opened_then_core_range_and_skips_recent()
    {
        using var db = new TempDatabase();
        var repo = db.Repository;
        repo.UpsertListings(
        [
            Wines.Listing("plain"),
            Wines.Listing("basis") with { ProductSelection = "Basisutvalget" },
            Wines.Listing("opened"),
            Wines.Listing("favourite"),
            Wines.Listing("unavailable") with { AvailableOnline = false, AvailableInStores = false },
        ], DateTimeOffset.UtcNow);
        repo.SetFavorite("favourite", true);
        repo.MarkDetailsUnavailable("opened", DateTimeOffset.UtcNow);

        var order = new List<string>();
        while (repo.NextWineForRating(TimeSpan.FromDays(30)) is { } code)
        {
            order.Add(code);
            repo.TouchRatings(code, DateTimeOffset.UtcNow);
        }

        Assert.Equal(["favourite", "opened", "basis", "plain"], order);
    }

    [Fact]
    public async Task Rates_one_wine_per_step_logs_results_and_moves_on()
    {
        using var db = new TempDatabase();
        db.Repository.UpsertListings([Wines.Listing("1", "Ressia Gepu Langhe Nebbiolo"), Wines.Listing("2", "Other Wine")], DateTimeOffset.UtcNow);
        var vivino = new CountingSource("vivino", w => new SourceLookup(
            [new RatingCandidate { Name = "Ressia Gepu Langhe Nebbiolo", Score = 3.9 }], "q"));
        var service = new RatingService(db.Repository, [vivino]);
        var log = new ActivityLog();
        var updater = new BackgroundRatingUpdater(db.Repository, () => service, NoDetails(db.Repository), TimeSpan.FromMilliseconds(5), log);
        var rated = new List<string>();
        var both = new TaskCompletionSource();
        updater.WineRated += (_, code) =>
        {
            lock (rated)
            {
                rated.Add(code);
                if (rated.Count == 2)
                {
                    both.TrySetResult();
                }
            }
        };

        updater.Start();
        await both.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await updater.StopAsync();

        Assert.Equal(["1", "2"], rated.Order());
        Assert.Equal(78, db.Repository.GetWine("1")!.BestScore);
        Assert.Contains(log.Snapshot(), e => e.Source == "ratings" && e.Message.Contains("Ressia") && e.Level == ActivityLevel.Info);
        Assert.Contains(log.Snapshot(), e => e.Message.Contains("3.9 ★") && e.Level == ActivityLevel.Success);
        // Both checked: nothing left until they are 30 days old.
        Assert.Null(db.Repository.NextWineForRating(BackgroundRatingUpdater.RecheckAfter));
        Assert.False(updater.IsRunning);
    }

    [Fact]
    public async Task A_wine_whose_lookup_keeps_a_previous_match_is_not_picked_again_and_again()
    {
        using var db = new TempDatabase();
        db.Repository.UpsertListings([Wines.Listing("1", "Ressia Gepu Langhe Nebbiolo")], DateTimeOffset.UtcNow);
        db.Repository.SaveRating(new RatingResult
        {
            Code = "1", Source = "vivino", Status = RatingStatus.Matched, Score = 3.9, Score100 = 78,
            FetchedAt = DateTimeOffset.UtcNow.AddDays(-200),
        });
        // Search fails: the old match is kept (not overwritten), so no rating row changes.
        var failing = new CountingSource("vivino", _ => new SourceLookup([], "q", Error: "Web search failed"));
        var service = new RatingService(db.Repository, [failing]);
        var updater = new BackgroundRatingUpdater(db.Repository, () => service, NoDetails(db.Repository), TimeSpan.FromMilliseconds(5));
        var once = new TaskCompletionSource();
        updater.WineRated += (_, _) => once.TrySetResult();

        updater.Start();
        await once.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await updater.StopAsync();

        Assert.Equal(1, failing.Calls);
        Assert.Null(db.Repository.NextWineForRating(BackgroundRatingUpdater.RecheckAfter));
        Assert.Equal(RatingStatus.Matched, db.Repository.GetRatings("1").Single().Status);
    }

    [Fact]
    public async Task A_failed_lookup_is_retried_after_a_day_not_a_month()
    {
        using var db = new TempDatabase();
        db.Repository.UpsertListings([Wines.Listing("1", "Ressia Gepu Langhe Nebbiolo")], DateTimeOffset.UtcNow);
        var failing = new CountingSource("vivino", _ => new SourceLookup([], "q", Error: "Web search failed"));
        var service = new RatingService(db.Repository, [failing]);
        var updater = new BackgroundRatingUpdater(db.Repository, () => service, NoDetails(db.Repository), TimeSpan.FromMilliseconds(5));
        var once = new TaskCompletionSource();
        updater.WineRated += (_, _) => once.TrySetResult();

        updater.Start();
        await once.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await updater.StopAsync();

        // Not due again today …
        Assert.Null(db.Repository.NextWineForRating(BackgroundRatingUpdater.RecheckAfter));
        // … but due within about a day, not after 30.
        Assert.Equal("1", db.Repository.NextWineForRating(BackgroundRatingUpdater.RecheckAfter - TimeSpan.FromDays(1) - TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task Restart_with_unchanged_settings_does_not_interrupt_the_loop()
    {
        using var db = new TempDatabase();
        var service = new RatingService(db.Repository, []);
        var updater = new BackgroundRatingUpdater(db.Repository, () => service, NoDetails(db.Repository), TimeSpan.FromMinutes(5));
        var statuses = new List<string>();
        updater.Start();
        updater.StatusChanged += (_, s) => statuses.Add(s.Message);

        await updater.RestartAsync(true, TimeSpan.FromMinutes(5));

        Assert.True(updater.IsRunning);
        Assert.DoesNotContain("Background ratings are off", statuses);
        await updater.StopAsync();
    }

    [Fact]
    public async Task Backs_off_when_every_source_fails()
    {
        using var db = new TempDatabase();
        db.Repository.UpsertListings([Wines.Listing("1"), Wines.Listing("2")], DateTimeOffset.UtcNow);
        var failing = new CountingSource("vivino", _ => new SourceLookup([], "q", Error: "Web search failed"));
        var service = new RatingService(db.Repository, [failing]);
        var log = new ActivityLog();
        var updater = new BackgroundRatingUpdater(db.Repository, () => service, NoDetails(db.Repository), TimeSpan.FromMilliseconds(5), log);

        updater.Start();
        await Task.Delay(1500);
        await updater.StopAsync();

        // One wine, then a 15-minute pause: the second wine is not tried within the test.
        Assert.Equal(1, failing.Calls);
        Assert.Contains(log.Snapshot(), e => e.Level == ActivityLevel.Warning && e.Message.Contains("could not be reached"));
        Assert.Equal("Background ratings are off", updater.Status.Message);
    }

    [Fact]
    public void Activity_log_keeps_the_most_recent_entries()
    {
        var log = new ActivityLog(capacity: 3);
        var seen = new List<string>();
        log.EntryAdded += (_, e) => seen.Add(e.Message);

        foreach (var i in Enumerable.Range(1, 5))
        {
            log.Write("test", $"line {i}");
        }

        Assert.Equal(["line 3", "line 4", "line 5"], log.Snapshot().Select(e => e.Message));
        Assert.Equal(5, seen.Count);
    }

    [Fact]
    public async Task Logging_fetcher_records_requests_and_flags_refusals()
    {
        var log = new ActivityLog();
        var fetcher = new LoggingFetcher(new FakeFetcher().On("ok", "fine").On("busy", "slow down", 429), log, "web");

        await fetcher.GetAsync("https://www.vinmonopolet.no/ok", "*/*", CancellationToken.None);
        await fetcher.GetAsync("https://www.vinmonopolet.no/busy", "*/*", CancellationToken.None);

        var entries = log.Snapshot();
        Assert.Equal(ActivityLevel.Detail, entries[0].Level);
        Assert.StartsWith("GET vinmonopolet.no/ok → 200", entries[0].Message);
        Assert.Equal(ActivityLevel.Warning, entries[1].Level);
        Assert.Contains("→ 429", entries[1].Message);
    }
}
