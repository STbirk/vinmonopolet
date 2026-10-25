using Vinliste.Core.Data;
using Vinliste.Core.Diagnostics;

namespace Vinliste.Core.Sync;

/// <summary>
/// Keeps the catalogue fresh while the app is open by running the normal resumable catalogue
/// update very slowly (one listing page per <see cref="BackgroundLoop.Interval"/>), starting a
/// new pass when one completes. A full pass of ~1,150 pages takes about 19 hours of app time at
/// one page a minute; progress is stored after every page, so closing the app loses nothing.
/// </summary>
public sealed class BackgroundCatalogUpdater(CatalogSyncService catalog, TimeSpan pageDelay, ActivityLog? log = null)
    : BackgroundLoop(log, "catalog", pageDelay)
{
    /// <summary>Wait after a failed pass (network down, site error) before trying again.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(10);

    /// <summary>Unfinished slow passes are resumed for this long after their last saved page.</summary>
    public static readonly TimeSpan ResumeMaxAge = TimeSpan.FromDays(7);

    /// <summary>Raised after each pass that completed (the list may have changed a lot).</summary>
    public event EventHandler<CatalogSyncResult>? PassCompleted;

    /// <summary>The pause before each listing page.</summary>
    public TimeSpan PageDelay
    {
        get => Interval;
        set => Interval = value;
    }

    protected override async Task LoopAsync(CancellationToken ct)
    {
        var progress = new SyncProgressHandler(p =>
        {
            if (p.Stage == "Updating")
            {
                Write(p.Message);
            }
            SetStatus(new BackgroundUpdateStatus(
                true,
                p.Stage == "Counting" ? "Checking Vinmonopolet for changes…" : $"Checking for changes · page {p.Done:N0} of {p.Total:N0}",
                p.Done,
                p.Total,
                p.Stage == "Updating" ? DateTimeOffset.UtcNow : Status.LastPageAt));
        });

        while (!ct.IsCancellationRequested)
        {
            SetStatus(Status with { IsActive = true, Message = "Waiting to check Vinmonopolet for changes…" });
            CatalogSyncResult result;
            try
            {
                result = await catalog.RunAsync(null, progress, ct, new CatalogSyncOptions(PageDelay, ResumeMaxAge)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A database error (busy, disk full) outside the run's own handling: treat the
                // pass as failed and retry later rather than letting the loop die silently.
                result = new CatalogSyncResult(SyncRunStatus.Failed, 0, 0, 0, 0, ex.Message);
            }

            if (ct.IsCancellationRequested || result.Status == SyncRunStatus.Cancelled)
            {
                break;
            }
            if (result.Status == SyncRunStatus.Completed)
            {
                Write($"Pass complete: {result.ProductsSeen:N0} wines, {result.ProductsNew:N0} new, " +
                      $"{result.PriceChanges:N0} price changes, {result.Delisted:N0} gone", ActivityLevel.Success);
                SetStatus(Status with { Message = $"Up to date · last pass {DateTime.Now:g}: {result.ProductsNew:N0} new, {result.PriceChanges:N0} price changes" });
                PassCompleted?.Invoke(this, result);
                continue;
            }

            // Failed, or a fast update the user started holds the catalogue: try again later.
            var busy = result.Error == CatalogSyncService.AlreadyRunning;
            var wait = busy ? PageDelay : RetryDelay;
            var message = busy
                ? "Paused while another update runs"
                : $"Could not update ({FirstLine(result.Error)}); retrying at {DateTime.Now.Add(wait):t}";
            Write(message, busy ? ActivityLevel.Info : ActivityLevel.Warning);
            SetStatus(Status with { Message = message });
            if (!await WaitAsync(wait, ct).ConfigureAwait(false))
            {
                break;
            }
        }
    }

    /// <summary>Synchronous progress sink: <see cref="Progress{T}"/> would post to a captured context.</summary>
    private sealed class SyncProgressHandler(Action<SyncProgress> handler) : IProgress<SyncProgress>
    {
        public void Report(SyncProgress value) => handler(value);
    }
}
