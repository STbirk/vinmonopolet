using Vinliste.Core.Diagnostics;

namespace Vinliste.Core.Sync;

/// <summary>What a background loop is doing, for the status display.</summary>
public sealed record BackgroundUpdateStatus(bool IsActive, string Message, int PagesDone, int PagesTotal, DateTimeOffset? LastPageAt)
{
    public static readonly BackgroundUpdateStatus Stopped = new(false, "Background updates are off", 0, 0, null);
}

/// <summary>
/// A slow, long-running loop that runs while the app is open: start, stop (waiting for the
/// current step to finish saving) and restart with a new pace. Subclasses implement the loop.
/// </summary>
public abstract class BackgroundLoop(ActivityLog? log, string source, TimeSpan interval)
{
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    protected ActivityLog? Log => log;

    /// <summary>The pause between steps (catalogue pages, rated wines).</summary>
    public TimeSpan Interval { get; set; } = interval;

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

    /// <summary>Message shown while the loop is switched off.</summary>
    protected virtual string StoppedMessage => BackgroundUpdateStatus.Stopped.Message;

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
            Write($"Started · one step every {Describe(Interval)}");
            _loop = Task.Run(() => RunGuardedAsync(token));
        }
    }

    /// <summary>Stops the loop and waits for the current step to be saved. Never throws.</summary>
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
                // RunGuardedAsync handles its own errors; a fault here must not break stopping.
            }
        }
        var stopped = BackgroundUpdateStatus.Stopped with { Message = message ?? StoppedMessage };
        if (Status != stopped)
        {
            SetStatus(stopped);
        }
    }

    /// <summary>
    /// Applies a new pace or on/off state: stops the running loop, then starts it again if enabled.
    /// Nothing happens when it already runs at that pace, so saving unrelated settings does not
    /// interrupt a lookup in progress.
    /// </summary>
    public async Task RestartAsync(bool enabled, TimeSpan interval)
    {
        // Overlapping restarts (two quick saves) run one after another, so the last call wins.
        await _restartGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (enabled && IsRunning && Interval == interval)
            {
                return;
            }
            await StopAsync().ConfigureAwait(false);
            Interval = interval;
            if (enabled)
            {
                Start();
            }
        }
        finally
        {
            _restartGate.Release();
        }
    }

    private readonly SemaphoreSlim _restartGate = new(1, 1);

    protected abstract Task LoopAsync(CancellationToken ct);

    protected void SetStatus(BackgroundUpdateStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    protected void Write(string message, ActivityLevel level = ActivityLevel.Info) => log?.Write(source, message, level);

    /// <summary>Waits, returning false when the loop was stopped meanwhile.</summary>
    protected static async Task<bool> WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    protected static string Describe(TimeSpan delay) =>
        delay.TotalSeconds < 90 ? $"{delay.TotalSeconds:0} s" : $"{delay.TotalMinutes:0.#} min";

    /// <summary>The short version of an error for a status line.</summary>
    protected static string FirstLine(string? error)
    {
        var line = (error ?? "unknown error").Split('\n')[0].Trim();
        // Vinmonopolet's rate-limit reply is long JSON; the status line only needs the gist.
        return line.Contains("429", StringComparison.Ordinal) ? "the site asked us to slow down" : line.Length > 80 ? line[..80] + "…" : line;
    }

    private async Task RunGuardedAsync(CancellationToken ct)
    {
        try
        {
            await LoopAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Last resort: the loop stops, but says why instead of dying silently.
            Write($"Stopped by an unexpected error: {ex.Message}", ActivityLevel.Error);
            SetStatus(BackgroundUpdateStatus.Stopped with { Message = $"Stopped: {FirstLine(ex.Message)}" });
            return;
        }
        Write("Stopped");
    }
}
