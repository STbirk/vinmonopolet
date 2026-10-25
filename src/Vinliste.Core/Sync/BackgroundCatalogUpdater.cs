using Vinliste.Core.Data;

namespace Vinliste.Core.Sync;

/// <summary>What the background updater is doing, for the status display.</summary>
public sealed record BackgroundUpdateStatus(bool IsActive, string Message, int PagesDone, int PagesTotal, DateTimeOffset? LastPageAt)
{
    public static readonly BackgroundUpdateStatus Stopped = new(false, "Background updates are off", 0, 0, null);
}

/// <summary>
/// Keeps the catalogue fresh while the app is open by running the normal resumable catalogue
/// update very slowly (one listing page per <see cref="PageDelay"/>), starting a new pass when
/// one completes. A full pass of ~1,150 pages takes about 19 hours of app time at one page a
/// minute; progress is stored after every page, so closing the app loses nothing.
/// </summary>
public sealed class BackgroundCatalogUpdater(CatalogSyncService catalog, TimeSpan pageDelay)
{
    /// <summary>Wait after a failed pass (network down, site error) before trying again.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(10);

    /// <summary>Unfinished slow passes are resumed for this long.</summary>
    public static readonly TimeSpan ResumeMaxAge = TimeSpan.FromDays(7);

    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public TimeSpan PageDelay { get; set; } = pageDelay;

    public BackgroundUpdateStatus Status { get; private set; } = BackgroundUpdateStatus.Stopped;

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _loop is { IsCompleted: false };
            }
        }
    }

    /// <summary>Raised on a thread-pool thread whenever <see cref="Status"/> changes.</summary>
    public event EventHandler<BackgroundUpdateStatus>? StatusChanged;

    /// <summary>Raised after each pass that completed (the list may have changed a lot).</summary>
    public event EventHandler<CatalogSyncResult>? PassCompleted;

    public void Start()
    {
        lock (_lock)
        {
            if (_loop is { IsCompleted: false })
            {
                return;
            }
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => LoopAsync(token));
        }
    }

    /// <summary>Stops the loop and waits for the current page to be saved. Never throws.</summary>
    /// <param name="message">Status to show while stopped ("off", or "paused while you update").</param>
    public async Task StopAsync(string? message = null)
    {
        Task? loop;
        lock (_lock)
        {
            _cts?.Cancel();
            loop = _loop;
        }
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The loop handles its own errors; a fault here must not break stopping.
            }
        }
        SetStatus(message is null ? BackgroundUpdateStatus.Stopped : BackgroundUpdateStatus.Stopped with { Message = message });
    }

    /// <summary>Applies a new pace or on/off state: stops the running loop, then starts it again if enabled.</summary>
    public async Task RestartAsync(bool enabled, TimeSpan pageDelay)
    {
        await StopAsync().ConfigureAwait(false);
        PageDelay = pageDelay;
        if (enabled)
        {
            Start();
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var progress = new SyncProgressHandler(p => SetStatus(new BackgroundUpdateStatus(
            true,
            p.Stage == "Counting" ? "Checking Vinmonopolet for changes…" : $"Checking for changes · page {p.Done:N0} of {p.Total:N0}",
            p.Done,
            p.Total,
            p.Stage == "Updating" ? DateTimeOffset.UtcNow : Status.LastPageAt)));

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
                SetStatus(Status with { Message = $"Up to date · last pass {DateTime.Now:g}: {result.ProductsNew:N0} new, {result.PriceChanges:N0} price changes" });
                PassCompleted?.Invoke(this, result);
                continue;
            }

            // Failed, or a fast update the user started holds the catalogue: try again later.
            var wait = result.Error == CatalogSyncService.AlreadyRunning ? PageDelay : RetryDelay;
            SetStatus(Status with
            {
                Message = result.Error == CatalogSyncService.AlreadyRunning
                    ? "Paused while another update runs"
                    : $"Could not update ({FirstLine(result.Error)}); retrying at {DateTime.Now.Add(wait):t}",
            });
            try
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static string FirstLine(string? error)
    {
        var line = (error ?? "unknown error").Split('\n')[0].Trim();
        // Vinmonopolet's rate-limit reply is long JSON; the status line only needs the gist.
        return line.Contains("429", StringComparison.Ordinal) ? "Vinmonopolet asked us to slow down" : line.Length > 80 ? line[..80] + "…" : line;
    }

    private void SetStatus(BackgroundUpdateStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    /// <summary>Synchronous progress sink: <see cref="Progress{T}"/> would post to a captured context.</summary>
    private sealed class SyncProgressHandler(Action<SyncProgress> handler) : IProgress<SyncProgress>
    {
        public void Report(SyncProgress value) => handler(value);
    }
}
