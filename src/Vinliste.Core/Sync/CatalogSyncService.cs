using System.Text.Json;
using Vinliste.Core.Data;
using Vinliste.Core.Models;
using Vinliste.Core.Vinmonopolet;

namespace Vinliste.Core.Sync;

public sealed record SyncProgress(string Stage, int Done, int Total, string Message)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Done / Total, 0, 1);
}

public sealed record CatalogSyncResult(string Status, int ProductsSeen, int ProductsNew, int PriceChanges, int Delisted, string? Error);

/// <summary>
/// Pages through every wine category listing and upserts the catalogue. Progress is
/// persisted after each page, so a cancelled or crashed run resumes where it stopped.
/// </summary>
public sealed class CatalogSyncService(WineRepository repository, VinmonopoletClient client)
{
    public const string Kind = "catalog";

    /// <summary>A category is only delisted from when this run saw at least this share of its active wines.</summary>
    private const double MinSeenShareForDelisting = 0.8;

    // One catalogue run at a time per process: two loops resuming the same run row would corrupt it.
    private static readonly SemaphoreSlim RunGate = new(1, 1);

    private sealed record Cursor(
        int CategoryIndex,
        int Page,
        Dictionary<string, int> PagesPerCategory,
        Dictionary<string, int>? SeenPerCategory = null,
        List<string>? IncompleteCategories = null);

    public static bool IsRunning => RunGate.CurrentCount == 0;

    public async Task<CatalogSyncResult> RunAsync(
        IReadOnlyList<WineCategory>? categories,
        IProgress<SyncProgress>? progress,
        CancellationToken ct)
    {
        if (!await RunGate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
        {
            return new CatalogSyncResult(SyncRunStatus.Failed, 0, 0, 0, 0, "A catalogue update is already running.");
        }
        try
        {
            return await RunCoreAsync(categories ?? WineCategories.All, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            RunGate.Release();
        }
    }

    private async Task<CatalogSyncResult> RunCoreAsync(IReadOnlyList<WineCategory> categories, IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        var run = repository.GetResumableRun(Kind);
        var cursor = run?.CursorJson is { } json ? JsonSerializer.Deserialize<Cursor>(json) : null;
        if (run is null || cursor is null || !cursor.PagesPerCategory.Keys.SequenceEqual(categories.Select(c => c.Code)))
        {
            if (run is not null)
            {
                // An unfinished run we will not resume (too old, other categories): close it.
                repository.UpdateRun(run with { Status = SyncRunStatus.Failed, FinishedAt = DateTimeOffset.UtcNow, Error = "Superseded by a new run" });
            }
            run = null;
            cursor = null;
        }

        try
        {
            if (run is null)
            {
                progress?.Report(new SyncProgress("Counting", 0, categories.Count, "Counting pages per wine type…"));
                var pages = new Dictionary<string, int>();
                for (var i = 0; i < categories.Count; i++)
                {
                    var first = await client.GetListingPageAsync(categories[i].Code, 0, ct).ConfigureAwait(false);
                    pages[categories[i].Code] = Math.Max(1, first.TotalPages);
                    progress?.Report(new SyncProgress("Counting", i + 1, categories.Count, $"{categories[i].Name}: {first.TotalResults:N0} products"));
                }
                cursor = new Cursor(0, 0, pages, [], []);
                run = repository.StartRun(Kind, pages.Values.Sum(), JsonSerializer.Serialize(cursor));
            }
            else
            {
                run = run with { Status = SyncRunStatus.Running, Error = null, FinishedAt = null };
                repository.UpdateRun(run);
            }
        }
        catch (OperationCanceledException)
        {
            return new CatalogSyncResult(SyncRunStatus.Cancelled, 0, 0, 0, 0, null);
        }
        catch (Exception ex)
        {
            return new CatalogSyncResult(SyncRunStatus.Failed, 0, 0, 0, 0, ex.Message);
        }

        var seenPerCategory = cursor!.SeenPerCategory ?? [];
        var incomplete = cursor.IncompleteCategories ?? [];
        var total = cursor.PagesPerCategory.Values.Sum();
        try
        {
            for (var ci = cursor.CategoryIndex; ci < categories.Count; ci++)
            {
                var category = categories[ci];
                var categoryPages = cursor.PagesPerCategory[category.Code];
                var startPage = ci == cursor.CategoryIndex ? cursor.Page : 0;
                for (var page = startPage; page < categoryPages; page++)
                {
                    ct.ThrowIfCancellationRequested();
                    var listing = await client.GetListingPageAsync(category.Code, page, ct).ConfigureAwait(false);
                    if (listing.TotalPages > categoryPages)
                    {
                        // The catalogue grew during the run; follow it.
                        total += listing.TotalPages - categoryPages;
                        categoryPages = listing.TotalPages;
                        cursor.PagesPerCategory[category.Code] = categoryPages;
                    }

                    var upsert = repository.UpsertListings(listing.Wines, DateTimeOffset.UtcNow);
                    seenPerCategory[category.Code] = seenPerCategory.GetValueOrDefault(category.Code) + upsert.Seen;
                    // An empty page before the end is a glitch, not the end of the catalogue: finish
                    // this category without trusting it for delisting.
                    var endedEarly = listing.Wines.Count == 0 && page + 1 < categoryPages;
                    if (endedEarly && !incomplete.Contains(category.Code))
                    {
                        incomplete.Add(category.Code);
                    }

                    var done = PagesBefore(cursor, categories, ci) + page + 1;
                    var next = page + 1 < categoryPages && !endedEarly
                        ? new Cursor(ci, page + 1, cursor.PagesPerCategory, seenPerCategory, incomplete)
                        : new Cursor(ci + 1, 0, cursor.PagesPerCategory, seenPerCategory, incomplete);
                    run = run with
                    {
                        PagesDone = done,
                        PagesTotal = total,
                        ProductsSeen = run.ProductsSeen + upsert.Seen,
                        ProductsNew = run.ProductsNew + upsert.New,
                        PriceChanges = run.PriceChanges + upsert.PriceChanges,
                        CursorJson = JsonSerializer.Serialize(next),
                    };
                    repository.UpdateRun(run);
                    progress?.Report(new SyncProgress("Updating", done, total,
                        $"{category.Name}: page {page + 1} of {categoryPages} · {run.ProductsNew:N0} new · {run.PriceChanges:N0} price changes"));
                    if (endedEarly)
                    {
                        break;
                    }
                }
            }

            var delisted = Delist(categories, run, seenPerCategory, incomplete);
            run = run with { Status = SyncRunStatus.Completed, FinishedAt = DateTimeOffset.UtcNow, PagesDone = total, PagesTotal = total, CursorJson = null };
            repository.UpdateRun(run);
            progress?.Report(new SyncProgress("Done", total, total,
                $"Catalogue updated: {run.ProductsSeen:N0} wines, {run.ProductsNew:N0} new, {run.PriceChanges:N0} price changes, {delisted:N0} delisted"));
            return new CatalogSyncResult(run.Status, run.ProductsSeen, run.ProductsNew, run.PriceChanges, delisted, null);
        }
        catch (OperationCanceledException)
        {
            run = run with { Status = SyncRunStatus.Cancelled, FinishedAt = DateTimeOffset.UtcNow };
            repository.UpdateRun(run);
            return new CatalogSyncResult(run.Status, run.ProductsSeen, run.ProductsNew, run.PriceChanges, 0, null);
        }
        catch (Exception ex)
        {
            run = run with { Status = SyncRunStatus.Failed, FinishedAt = DateTimeOffset.UtcNow, Error = ex.Message };
            repository.UpdateRun(run);
            return new CatalogSyncResult(run.Status, run.ProductsSeen, run.ProductsNew, run.PriceChanges, 0, ex.Message);
        }
    }

    /// <summary>
    /// Delists wines missing from this run and the previous completed one, only in categories
    /// this run paged completely and where it saw a plausible share of the known wines.
    /// </summary>
    private int Delist(IReadOnlyList<WineCategory> categories, SyncRun run, Dictionary<string, int> seen, List<string> incomplete)
    {
        var previousStart = repository.PreviousCompletedRunStart(Kind, run.Id);
        if (previousStart is null)
        {
            return 0;
        }
        var trusted = categories
            .Select(c => c.Code)
            .Where(code => !incomplete.Contains(code))
            .Where(code => seen.GetValueOrDefault(code) >= MinSeenShareForDelisting * repository.CountActive(code))
            .ToList();
        return trusted.Count == 0 ? 0 : repository.MarkDelisted(trusted, previousStart.Value);
    }

    private static int PagesBefore(Cursor cursor, IReadOnlyList<WineCategory> categories, int categoryIndex) =>
        categories.Take(categoryIndex).Sum(c => cursor.PagesPerCategory[c.Code]);
}

/// <summary>Fetches product-page details for wines whose details are missing or stale.</summary>
public sealed class DetailsSyncService(WineRepository repository, VinmonopoletClient client)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    /// <summary>Fetches and stores one wine's details. Returns null when the product page has none.</summary>
    public async Task<WineDetails?> RefreshAsync(string code, CancellationToken ct)
    {
        WineDetails? details;
        try
        {
            details = await client.GetDetailsAsync(code, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            details = null;
        }
        if (details is null || details.Code != code)
        {
            // Remember the miss so the background job moves on to other wines.
            repository.MarkDetailsUnavailable(code, DateTimeOffset.UtcNow);
            return null;
        }
        repository.SaveDetails(details, DateTimeOffset.UtcNow);
        return repository.GetDetails(code);
    }

    /// <summary>Fetches details for up to <paramref name="limit"/> wines; returns how many were saved.</summary>
    public async Task<int> RunAsync(IReadOnlyCollection<string>? preferCodes, int limit, IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        var codes = repository.CodesNeedingDetails(MaxAge, limit, preferCodes);
        var done = 0;
        var saved = 0;
        var failed = 0;
        foreach (var code in codes)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await RefreshAsync(code, ct).ConfigureAwait(false) is not null)
                {
                    saved++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A network error on one wine must not stop the batch; it is retried next run.
                failed++;
            }
            done++;
            progress?.Report(new SyncProgress("Details", done, codes.Count,
                $"Wine details {done:N0} of {codes.Count:N0}" + (failed > 0 ? $" ({failed} failed)" : "")));
        }
        return saved;
    }
}

/// <summary>Fetches stock in every store that has the wine, nearest first.</summary>
public sealed class StockService(WineRepository repository, VinmonopoletClient client)
{
    // Oslo sentrum; replaced by the user's location or favourite store in settings.
    public const double DefaultLatitude = 59.9133;
    public const double DefaultLongitude = 10.7389;

    public async Task<IReadOnlyList<StoreStock>> RefreshAsync(string code, double? latitude, double? longitude, CancellationToken ct)
    {
        var stock = await client.GetAllStockAsync(code, latitude ?? DefaultLatitude, longitude ?? DefaultLongitude, ct).ConfigureAwait(false);
        repository.SaveStock(code, stock, DateTimeOffset.UtcNow);
        return repository.GetStock(code);
    }
}
