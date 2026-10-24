using Vinliste.Core.Data;
using Vinliste.Core.Models;
using Vinliste.Core.Vinmonopolet;

namespace Vinliste.Core.Tests;

public class WineRepositoryTests
{
    [Fact]
    public void New_database_is_migrated_to_latest_version()
    {
        using var db = new TempDatabase();

        Assert.Equal(VinlisteDatabase.LatestVersion, db.Database.GetSchemaVersion());
        // Opening again must not re-run migrations.
        var reopened = new VinlisteDatabase(db.Database.Path);
        Assert.Equal(VinlisteDatabase.LatestVersion, reopened.GetSchemaVersion());
    }

    [Fact]
    public void UpsertListings_counts_new_wines_and_records_price_history_only_on_change()
    {
        using var db = new TempDatabase();
        var repo = db.Repository;

        var first = repo.UpsertListings([Wines.Listing("1", price: 200), Wines.Listing("2", price: 300)], DateTimeOffset.UtcNow);
        Assert.Equal(new UpsertResult(2, 2, 0), first);

        var second = repo.UpsertListings([Wines.Listing("1", price: 200), Wines.Listing("2", price: 279)], DateTimeOffset.UtcNow);
        Assert.Equal(new UpsertResult(2, 0, 1), second);

        Assert.Single(repo.GetPriceHistory("1"));
        Assert.Equal([300.0, 279.0], repo.GetPriceHistory("2").Select(p => p.Price));

        var wine = repo.GetWine("2")!;
        Assert.Equal(279, wine.Price);
        Assert.Equal(300, wine.PreviousPrice);
        Assert.Equal(-21, wine.PriceChange);
    }

    [Fact]
    public void MarkDelisted_only_hits_unseen_wines_in_synced_categories()
    {
        using var db = new TempDatabase();
        var repo = db.Repository;
        var old = DateTimeOffset.UtcNow.AddDays(-2);
        repo.UpsertListings([Wines.Listing("gone"), Wines.Listing("white", category: "hvitvin")], old);
        var runStart = DateTimeOffset.UtcNow.AddMinutes(-1);
        repo.UpsertListings([Wines.Listing("kept")], DateTimeOffset.UtcNow);

        var delisted = repo.MarkDelisted(["rødvin"], runStart);

        Assert.Equal(1, delisted);
        Assert.Equal(["kept"], repo.Query(new WineQuery { CategoryCodes = ["rødvin"] }).Select(w => w.Code));
        Assert.NotNull(repo.GetWine("gone")!.DelistedAt);
        Assert.Null(repo.GetWine("white")!.DelistedAt);

        // Seen again later: back in the list.
        repo.UpsertListings([Wines.Listing("gone")], DateTimeOffset.UtcNow);
        Assert.Null(repo.GetWine("gone")!.DelistedAt);
    }

    [Fact]
    public void SaveDetails_round_trips_and_fills_wine_columns()
    {
        using var db = new TempDatabase();
        var repo = db.Repository;
        repo.UpsertListings([Wines.Listing("11929001", "Biondi Santi Rosso di Montalcino")], DateTimeOffset.UtcNow);
        var parsed = VinmonopoletParser.ParseProduct(Fixtures.Read("vmp_product_full.json"));

        repo.SaveDetails(parsed, DateTimeOffset.UtcNow);
        var details = repo.GetDetails("11929001")!;

        Assert.Equal(parsed.Taste, details.Taste);
        Assert.Equal(parsed.Fullness, details.Fullness);
        Assert.Equal("Brunello", Assert.Single(details.Grapes).Name);
        Assert.Equal(3, details.FoodPairings.Count);
        var wine = repo.GetWine("11929001")!;
        Assert.Equal("Biondi Santi Il Greppo", wine.Producer);
        Assert.Equal("2022", wine.Vintage);
        Assert.NotNull(wine.DetailsUpdatedAt);

        // Saving again replaces child rows instead of duplicating them.
        repo.SaveDetails(parsed, DateTimeOffset.UtcNow);
        Assert.Single(repo.GetDetails("11929001")!.Grapes);
    }

    [Fact]
    public void Query_filters_by_text_including_grape_and_producer()
    {
        using var db = new TempDatabase();
        var repo = db.Repository;
        repo.UpsertListings([Wines.Listing("11929001", "Biondi Santi Rosso di Montalcino"), Wines.Listing("2", "Other Wine")], DateTimeOffset.UtcNow);
        repo.SaveDetails(VinmonopoletParser.ParseProduct(Fixtures.Read("vmp_product_full.json")), DateTimeOffset.UtcNow);

        Assert.Equal(["11929001"], repo.Query(new WineQuery { Text = "brunello" }).Select(w => w.Code));
        Assert.Equal(["11929001"], repo.Query(new WineQuery { Text = "greppo rosso" }).Select(w => w.Code));
        Assert.Equal(["11929001"], repo.Query(new WineQuery { Text = "11929001" }).Select(w => w.Code));
        Assert.Empty(repo.Query(new WineQuery { Text = "greppo other" }));
    }

    [Fact]
    public void Query_text_ignores_case_and_accents_beyond_ascii_and_escapes_wildcards()
    {
        using var db = new TempDatabase();
        var repo = db.Repository;
        repo.UpsertListings([Wines.Listing("1", "Øko Rosé"), Wines.Listing("2", "Château Margaux"), Wines.Listing("3", "Plain")], DateTimeOffset.UtcNow);

        Assert.Equal(["1"], repo.Query(new WineQuery { Text = "øko" }).Select(w => w.Code));
        Assert.Equal(["1"], repo.Query(new WineQuery { Text = "oko rose" }).Select(w => w.Code));
        Assert.Equal(["2"], repo.Query(new WineQuery { Text = "CHÂTEAU" }).Select(w => w.Code));
        Assert.Equal(["2"], repo.Query(new WineQuery { Text = "chateau" }).Select(w => w.Code));
        Assert.Empty(repo.Query(new WineQuery { Text = "_" }));
        Assert.Empty(repo.Query(new WineQuery { Text = "%" }));
    }

    [Fact]
    public void Old_unfinished_runs_are_not_resumable()
    {
        using var db = new TempDatabase();
        var run = db.Repository.StartRun("catalog", 10, "{}");

        Assert.NotNull(db.Repository.GetResumableRun("catalog"));
        Assert.Null(db.Repository.GetResumableRun("catalog", TimeSpan.Zero));
    }

    [Fact]
    public void Query_filters_price_score_favourites_and_sorts_nulls_last()
    {
        using var db = new TempDatabase();
        var repo = db.Repository;
        repo.UpsertListings([Wines.Listing("a", price: 150), Wines.Listing("b", price: 400), Wines.Listing("c", price: null)], DateTimeOffset.UtcNow);
        repo.SaveRating(new RatingResult { Code = "a", Source = "vivino", Status = RatingStatus.Matched, Score = 4.0, Score100 = 80 });
        repo.SaveRating(new RatingResult { Code = "b", Source = "falstaff", Status = RatingStatus.Matched, Score = 93, Score100 = 93 });
        repo.SaveRating(new RatingResult { Code = "b", Source = "vivino", Status = RatingStatus.Matched, Score = 4.5, Score100 = 90 });
        repo.SetFavorite("b", true);

        Assert.Equal(["a"], repo.Query(new WineQuery { MaxPrice = 200 }).Select(w => w.Code));
        Assert.Equal(["b"], repo.Query(new WineQuery { MinScore = 85 }).Select(w => w.Code));
        Assert.Equal(["b"], repo.Query(new WineQuery { FavoritesOnly = true }).Select(w => w.Code));
        Assert.Equal(["b", "a", "c"], repo.Query(new WineQuery { Sort = WineSort.Price, Descending = true }).Select(w => w.Code));
        Assert.Equal(["a", "b", "c"], repo.Query(new WineQuery { Sort = WineSort.Price }).Select(w => w.Code));

        var b = repo.GetWine("b")!;
        Assert.Equal(91.5, b.BestScore);
        Assert.Equal(2, b.RatingSources);
        Assert.Equal(3, repo.Count(new WineQuery()));
    }

    [Fact]
    public void Non_matched_ratings_do_not_count_towards_best_score()
    {
        using var db = new TempDatabase();
        var repo = db.Repository;
        repo.UpsertListings([Wines.Listing("a")], DateTimeOffset.UtcNow);
        repo.SaveRating(new RatingResult { Code = "a", Source = "vivino", Status = RatingStatus.Matched, Score = 4.0, Score100 = 80 });
        repo.SaveRating(new RatingResult { Code = "a", Source = "falstaff", Status = RatingStatus.NoMatch });

        Assert.Equal(80, repo.GetWine("a")!.BestScore);
        Assert.Equal(2, repo.GetRatings("a").Count);

        // A later no-match for the same source replaces the match.
        repo.SaveRating(new RatingResult { Code = "a", Source = "vivino", Status = RatingStatus.NoMatch });
        Assert.Null(repo.GetWine("a")!.BestScore);
    }

    [Fact]
    public void SaveStock_replaces_previous_stock_and_upserts_stores()
    {
        using var db = new TempDatabase();
        var repo = db.Repository;
        repo.UpsertListings([Wines.Listing("11929001")], DateTimeOffset.UtcNow);
        var stock = VinmonopoletParser.ParseStock(Fixtures.Read("vmp_stock.json"), DateTimeOffset.UtcNow);

        repo.SaveStock("11929001", stock, DateTimeOffset.UtcNow);
        repo.SaveStock("11929001", stock.Take(2).ToList(), DateTimeOffset.UtcNow);

        Assert.Equal(2, repo.GetStock("11929001").Count);
        Assert.Equal(8, repo.GetStores().Count);
        Assert.Equal(["11929001"], repo.Query(new WineQuery { InStockAtStoreId = "178" }).Select(w => w.Code));
    }

    [Fact]
    public void CodesNeedingDetails_prefers_requested_codes_and_skips_fresh_ones()
    {
        using var db = new TempDatabase();
        var repo = db.Repository;
        repo.UpsertListings([Wines.Listing("1"), Wines.Listing("2"), Wines.Listing("11929001")], DateTimeOffset.UtcNow);
        repo.SaveDetails(VinmonopoletParser.ParseProduct(Fixtures.Read("vmp_product_full.json")), DateTimeOffset.UtcNow);

        var codes = repo.CodesNeedingDetails(TimeSpan.FromDays(30), 10, ["2", "11929001"]);

        Assert.Equal(["2", "1"], codes);
    }

    [Fact]
    public void Settings_and_runs_round_trip()
    {
        using var db = new TempDatabase();
        var repo = db.Repository;
        repo.SetSetting("store", "178");
        Assert.Equal("178", repo.GetSetting("store"));

        var run = repo.StartRun("catalog", 10, "{}");
        Assert.Equal(run.Id, repo.GetResumableRun("catalog")!.Id);
        Assert.Null(repo.PreviousCompletedRunStart("catalog", run.Id));
        repo.UpdateRun(run with { Status = SyncRunStatus.Completed, FinishedAt = DateTimeOffset.UtcNow, PagesDone = 10 });
        Assert.Null(repo.GetResumableRun("catalog"));
        Assert.NotNull(repo.GetStats().LastSync);
    }
}
