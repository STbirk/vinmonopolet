using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vinliste.App.Services;
using Vinliste.Core.Diagnostics;

namespace Vinliste.App.ViewModels;

public sealed record ActivityLine(string Time, string Source, string Message, ActivityLevel Level)
{
    public static ActivityLine From(ActivityEntry entry) =>
        new(entry.Time.LocalDateTime.ToString("HH:mm:ss"), entry.Source, entry.Message, entry.Level);

    public override string ToString() => $"{Time} {Source,-8} {Message}";
}

/// <summary>The mini terminal: a live view of what background work is doing.</summary>
public sealed partial class ActivityViewModel : ObservableObject
{
    private const int MaxLines = 500;

    private readonly AppServices _services;
    private readonly Dispatcher _dispatcher;

    public ActivityViewModel(AppServices services, Dispatcher dispatcher)
    {
        _services = services;
        _dispatcher = dispatcher;
        _isOpen = services.Settings.ShowActivityPanel;
        _showWebRequests = services.Settings.ShowWebRequests;
        Reload();
        // Entries arrive on worker threads; the list is only touched on the UI thread.
        services.Activity.EntryAdded += (_, entry) => _dispatcher.BeginInvoke(() => Add(entry));
    }

    public ObservableCollection<ActivityLine> Lines { get; } = [];

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private bool _showWebRequests;
    [ObservableProperty] private string _latest = "";

    /// <summary>Raised after a line is added (the view scrolls to it).</summary>
    public event EventHandler? LineAdded;

    partial void OnIsOpenChanged(bool value)
    {
        _services.Settings.ShowActivityPanel = value;
        _services.SaveSettings();
    }

    partial void OnShowWebRequestsChanged(bool value)
    {
        _services.Settings.ShowWebRequests = value;
        _services.SaveSettings();
        Reload();
    }

    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen;

    [RelayCommand]
    private void Clear()
    {
        _services.Activity.Clear();
        Lines.Clear();
        Latest = "";
    }

    [RelayCommand]
    private void Copy()
    {
        if (Lines.Count == 0)
        {
            return;
        }
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, Lines));
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another program holds the clipboard; copying again in a moment works.
        }
    }

    private void Add(ActivityEntry entry)
    {
        if (entry.Level != ActivityLevel.Detail)
        {
            Latest = $"{entry.Source}: {entry.Message.Trim()}";
        }
        if (!Visible(entry))
        {
            return;
        }
        Lines.Add(ActivityLine.From(entry));
        while (Lines.Count > MaxLines)
        {
            Lines.RemoveAt(0);
        }
        LineAdded?.Invoke(this, EventArgs.Empty);
    }

    private void Reload()
    {
        Lines.Clear();
        foreach (var entry in _services.Activity.Snapshot().Where(Visible).TakeLast(MaxLines))
        {
            Lines.Add(ActivityLine.From(entry));
        }
        LineAdded?.Invoke(this, EventArgs.Empty);
    }

    private bool Visible(ActivityEntry entry) => ShowWebRequests || entry.Level != ActivityLevel.Detail;
}
