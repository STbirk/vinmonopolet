using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vinliste.Core.Sync;

namespace Vinliste.App.ViewModels;

/// <summary>
/// The one long-running background job at a time (catalogue update, details, ratings),
/// shown in the status bar with progress and a cancel button.
/// </summary>
public sealed partial class JobViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _message = "Ready";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isIndeterminate;

    public event EventHandler? Finished;

    /// <summary>Runs <paramref name="work"/> unless another job is running. Returns false when busy.</summary>
    public bool TryStart(string title, Func<IProgress<SyncProgress>, CancellationToken, Task<string>> work)
    {
        if (IsBusy)
        {
            return false;
        }
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsBusy = true;
        Title = title;
        Message = "Starting…";
        Progress = 0;
        IsIndeterminate = true;

        // Progress<T> captures the UI context, so reports land on the UI thread.
        var progress = new Progress<SyncProgress>(p =>
        {
            Message = p.Message;
            IsIndeterminate = p.Total <= 0;
            Progress = p.Fraction;
        });
        _ = RunAsync(work, progress, token);
        return true;
    }

    private async Task RunAsync(Func<IProgress<SyncProgress>, CancellationToken, Task<string>> work, IProgress<SyncProgress> progress, CancellationToken token)
    {
        try
        {
            Message = await Task.Run(() => work(progress, token), token);
        }
        catch (OperationCanceledException)
        {
            Message = $"{Title} stopped. Results so far are saved.";
        }
        catch (Exception ex)
        {
            Message = $"{Title} failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            IsIndeterminate = false;
            Progress = 0;
            _cts?.Dispose();
            _cts = null;
            Finished?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        Message = "Stopping…";
        _cts?.Cancel();
    }
}
