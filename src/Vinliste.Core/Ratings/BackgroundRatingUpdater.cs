using Vinliste.Core.Data;
using Vinliste.Core.Diagnostics;
using Vinliste.Core.Models;
using Vinliste.Core.Sync;

namespace Vinliste.Core.Ratings;

/// <summary>
/// Collects ratings very slowly while the app is open: one wine per <see cref="BackgroundLoop.Interval"/>
/// (default five minutes), fetching its product details first when missing (the producer and
/// vintage make matching better). Favourites come first, then wines the user has opened, then the
/// core range; every wine is re-checked after <see cref="RecheckAfter"/>.
/// </summary>
public sealed class BackgroundRatingUpdater(
    WineRepository repository,
    Func<RatingService> ratings,
    DetailsSyncService details,
    TimeSpan interval,
    ActivityLog? log = null)
    : BackgroundLoop(log, "ratings", interval)
{
    public static readonly TimeSpan RecheckAfter = TimeSpan.FromDays(30);

    /// <summary>Wait when every source failed (search engine down or refusing) before the next wine.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(15);

    /// <summary>Wait when every available wine has recent ratings.</summary>
    public static readonly TimeSpan IdleDelay = TimeSpan.FromMinutes(30);

    /// <summary>Raised after a wine's ratings were checked (its row and score may have changed).</summary>
    public event EventHandler<string>? WineRated;

    protected override string StoppedMessage => "Background ratings are off";

    protected override async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            SetStatus(Status with { IsActive = true, Message = $"Next wine in {Describe(Interval)}" });
            if (!await WaitAsync(Interval, ct).ConfigureAwait(false))
            {
                return;
            }

            var code = repository.NextWineForRating(RecheckAfter);
            if (code is null || repository.GetWine(code) is not { } wine)
            {
                Write("Every available wine has recent ratings; checking again later");
                SetStatus(Status with { Message = "All available wines have recent ratings" });
                if (!await WaitAsync(IdleDelay, ct).ConfigureAwait(false))
                {
                    return;
                }
                continue;
            }

            SetStatus(Status with { Message = $"Rating {wine.Name}" });
            Write($"Rating {wine.Name} ({wine.Code})");
            var allFailed = await RateOneAsync(wine, ct).ConfigureAwait(false);
            WineRated?.Invoke(this, code);

            if (allFailed)
            {
                var message = $"Rating sites could not be reached; trying again at {DateTime.Now.Add(RetryDelay):t}";
                Write(message, ActivityLevel.Warning);
                SetStatus(Status with { Message = message });
                if (!await WaitAsync(RetryDelay, ct).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
    }

    /// <summary>Rates one wine; returns true when every source failed (so the caller backs off).</summary>
    private async Task<bool> RateOneAsync(WineRow wine, CancellationToken ct)
    {
        if (wine.DetailsUpdatedAt is null)
        {
            try
            {
                await details.RefreshAsync(wine.Code, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Write($"  details unavailable: {FirstLine(ex.Message)}", ActivityLevel.Warning);
            }
        }

        IReadOnlyList<RatingResult> results;
        try
        {
            results = await ratings().RateAsync(wine.Code, force: false, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Write($"  failed: {FirstLine(ex.Message)}", ActivityLevel.Error);
            repository.TouchRatings(wine.Code, RetryTomorrow());
            return false;
        }
        // Move on even when nothing new was saved (fresh results, or an error kept an older match),
        // but come back tomorrow, not in a month, when a source failed: its error is only cached a day.
        var failedNow = results.Any(r => r.Status is RatingStatus.Error or RatingStatus.Blocked
                                         && DateTimeOffset.UtcNow - r.FetchedAt < TimeSpan.FromMinutes(5));
        repository.TouchRatings(wine.Code, failedNow ? RetryTomorrow() : DateTimeOffset.UtcNow);

        foreach (var result in results)
        {
            Write($"  {Describe(result)}", result.Status switch
            {
                RatingStatus.Matched => ActivityLevel.Success,
                RatingStatus.Error or RatingStatus.Blocked => ActivityLevel.Warning,
                _ => ActivityLevel.Info,
            });
        }
        var best = repository.GetWine(wine.Code)?.BestScore;
        Write(best is null ? "  no score found" : $"  best rating {best:0.#}", best is null ? ActivityLevel.Info : ActivityLevel.Success);

        var fresh = results.Where(r => DateTimeOffset.UtcNow - r.FetchedAt < TimeSpan.FromMinutes(5)).ToList();
        return fresh.Count > 0 && fresh.All(r => r.Status == RatingStatus.Error);
    }

    /// <summary>A "last checked" stamp that makes the wine due again in about a day.</summary>
    private static DateTimeOffset RetryTomorrow() => DateTimeOffset.UtcNow - RecheckAfter + TimeSpan.FromDays(1);

    private static string Describe(RatingResult r) => r.Status switch
    {
        RatingStatus.Matched => $"{r.Source,-15} {(r.Source == "vivino" ? $"{r.Score:0.0} ★" : $"{r.Score:0.#} pts")}  {r.MatchedName}",
        RatingStatus.MatchedNoScore => $"{r.Source,-15} found, no score yet  {r.MatchedName}",
        RatingStatus.NoMatch => $"{r.Source,-15} not found",
        RatingStatus.Blocked => $"{r.Source,-15} blocked by the site",
        _ => $"{r.Source,-15} error: {FirstLine(r.Error)}",
    };
}
