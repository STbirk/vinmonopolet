using Vinliste.Core.Data;
using Vinliste.Core.Models;
using Vinliste.Core.Sync;

namespace Vinliste.Core.Ratings;

/// <summary>Collects ratings for wines from every source, caches them in the database and keeps best scores current.</summary>
public sealed class RatingService(WineRepository repository, IReadOnlyList<IRatingSource> sources, double matchThreshold = 82)
{
    public IReadOnlyList<IRatingSource> Sources => sources;

    /// <summary>How long a stored result is trusted before the source is asked again.</summary>
    public static TimeSpan MaxAge(string status) => status switch
    {
        RatingStatus.Matched => TimeSpan.FromDays(90),
        RatingStatus.MatchedNoScore or RatingStatus.NoMatch => TimeSpan.FromDays(30),
        _ => TimeSpan.FromDays(1),
    };

    public static WineIdentity IdentityFor(WineRow wine, WineDetails? details) => new()
    {
        Code = wine.Code,
        Name = wine.Name,
        Producer = details?.Producer ?? wine.Producer,
        Vintage = details?.Vintage ?? wine.Vintage,
        Country = wine.Country,
        District = wine.District,
        SubDistrict = wine.SubDistrict,
        CategoryCode = wine.CategoryCode,
    };

    /// <summary>Marks a result copied from the same wine in another bottle size (in <see cref="RatingResult.SearchQuery"/>).</summary>
    public const string SharedPrefix = "shared from ";

    /// <summary>
    /// Rates one wine with every source in parallel. Fresh stored results are reused unless
    /// <paramref name="force"/>; so are fresh results for the same wine in another bottle size.
    /// A source that matched the wine in another vintage gets that match as a hint. New results
    /// are shared with the other bottle sizes.
    /// </summary>
    public async Task<IReadOnlyList<RatingResult>> RateAsync(string code, bool force, CancellationToken ct, IReadOnlyCollection<string>? onlySources = null)
    {
        var wine = repository.GetWine(code) ?? throw new InvalidOperationException($"Wine {code} is not in the catalogue.");
        var identity = IdentityFor(wine, repository.GetDetails(code));
        var existing = repository.GetRatings(code).ToDictionary(r => r.Source);
        var due = sources
            .Where(s => onlySources is null || onlySources.Contains(s.Id))
            .Where(s => force || !existing.TryGetValue(s.Id, out var stored) || !IsFresh(stored))
            .ToList();

        // The same wine in another bottle size, checked recently: copy instead of searching again.
        var siblings = repository.GetSiblingCodes(code);
        var siblingResults = siblings.SelectMany(repository.GetRatings).Where(r => IsShareable(r) && IsFresh(r)).ToList();
        var toLookUp = new List<IRatingSource>();
        foreach (var source in due)
        {
            var shared = force ? null : siblingResults.Where(r => r.Source == source.Id).MaxBy(r => r.FetchedAt);
            if (shared is not null)
            {
                repository.SaveRating(shared with { Code = code, SearchQuery = SharedPrefix + shared.Code });
            }
            else
            {
                toLookUp.Add(source);
            }
        }

        var lookups = toLookUp.Select(source => RateWithSourceAsync(source, identity, repository.FindRatingLink(code, source.Id), ct));
        var saved = new List<RatingResult>();
        foreach (var result in await Task.WhenAll(lookups).ConfigureAwait(false))
        {
            // A transient failure must not wipe a score we already have; the match is kept
            // and the source is asked again after its normal refresh interval.
            var transient = result.Status is RatingStatus.Error or RatingStatus.Blocked;
            if (transient && existing.TryGetValue(result.Source, out var previous) && previous.Status == RatingStatus.Matched)
            {
                continue;
            }
            repository.SaveRating(result);
            saved.Add(result);
        }

        ShareWithSiblings(code, siblings, saved.Where(IsShareable));
        return repository.GetRatings(code);
    }

    /// <summary>Copies new results to the other bottle sizes, without replacing a newer result or a match with a non-match.</summary>
    private void ShareWithSiblings(string code, IReadOnlyList<string> siblings, IEnumerable<RatingResult> results)
    {
        var list = results.ToList();
        if (list.Count == 0)
        {
            return;
        }
        foreach (var sibling in siblings)
        {
            var theirs = repository.GetRatings(sibling).ToDictionary(r => r.Source);
            foreach (var result in list)
            {
                if (theirs.TryGetValue(result.Source, out var current)
                    && (current.FetchedAt >= result.FetchedAt
                        || (current.Status == RatingStatus.Matched && result.Status != RatingStatus.Matched && IsFresh(current))))
                {
                    continue;
                }
                repository.SaveRating(result with { Code = sibling, SearchQuery = SharedPrefix + code });
            }
        }
    }

    private static bool IsFresh(RatingResult result) => DateTimeOffset.UtcNow - result.FetchedAt <= MaxAge(result.Status);

    /// <summary>Results that say something about the wine itself; errors and blocks only describe one attempt.</summary>
    private static bool IsShareable(RatingResult result) =>
        result.Status is RatingStatus.Matched or RatingStatus.MatchedNoScore or RatingStatus.NoMatch;

    /// <summary>Rates many wines, two at a time, reporting progress per wine.</summary>
    public async Task<int> RunAsync(IReadOnlyList<string> codes, bool missingOnly, IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        var done = 0;
        var matched = 0;
        using var gate = new SemaphoreSlim(2);
        var tasks = codes.Select(async code =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                IReadOnlyCollection<string>? only = null;
                if (missingOnly)
                {
                    var have = repository.GetRatings(code).Where(r => r.Status == RatingStatus.Matched).Select(r => r.Source).ToHashSet();
                    only = sources.Select(s => s.Id).Where(id => !have.Contains(id)).ToList();
                    if (only.Count == 0)
                    {
                        return;
                    }
                }
                var results = await RateAsync(code, force: false, ct, only).ConfigureAwait(false);
                if (results.Any(r => r.Status == RatingStatus.Matched))
                {
                    Interlocked.Increment(ref matched);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One wine failing (deleted meanwhile, database busy) must not stop the batch.
            }
            finally
            {
                var count = Interlocked.Increment(ref done);
                progress?.Report(new SyncProgress("Ratings", count, codes.Count, $"Ratings {count:N0} of {codes.Count:N0} · {Volatile.Read(ref matched):N0} with a score"));
                gate.Release();
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return matched;
    }

    private async Task<RatingResult> RateWithSourceAsync(IRatingSource source, WineIdentity wine, RatingResult? hint, CancellationToken ct)
    {
        SourceLookup lookup;
        try
        {
            lookup = await source.LookupAsync(wine, matchThreshold, ct, hint).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new RatingResult { Code = wine.Code, Source = source.Id, Status = RatingStatus.Error, Error = ex.Message };
        }

        if (lookup.Error is not null)
        {
            return new RatingResult { Code = wine.Code, Source = source.Id, Status = RatingStatus.Error, SearchQuery = lookup.Query, Error = lookup.Error };
        }
        if (lookup.Blocked)
        {
            return new RatingResult
            {
                Code = wine.Code, Source = source.Id, Status = RatingStatus.Blocked, SearchQuery = lookup.Query,
                Error = $"{source.Label} blocked the request",
            };
        }

        // The best match wins; a scored candidate only wins ties (see WineMatcher), so a scored
        // sibling never beats a better unscored match of the right wine.
        var match = WineMatcher.BestMatch(wine, lookup.Candidates, matchThreshold);
        if (match is null)
        {
            var closest = WineMatcher.BestConfidence(wine, lookup.Candidates);
            return new RatingResult
            {
                Code = wine.Code, Source = source.Id, Status = RatingStatus.NoMatch, SearchQuery = lookup.Query,
                MatchConfidence = closest > 0 ? Math.Round(closest, 1) : null,
            };
        }

        var candidate = match.Candidate;
        return new RatingResult
        {
            Code = wine.Code,
            Source = source.Id,
            Status = candidate.Score is null ? RatingStatus.MatchedNoScore : RatingStatus.Matched,
            Score = candidate.Score,
            Score100 = candidate.Score is null ? null : Math.Round(source.ToScore100(candidate.Score.Value), 1),
            RatingCount = candidate.RatingCount,
            MatchedName = candidate.Producer is not null && !candidate.Name.Contains(candidate.Producer, StringComparison.OrdinalIgnoreCase)
                ? $"{candidate.Producer} {candidate.Name}"
                : candidate.Name,
            MatchedVintage = candidate.Vintage,
            Url = candidate.Url,
            MatchConfidence = Math.Round(match.Confidence, 1),
            SearchQuery = lookup.Query,
        };
    }
}
