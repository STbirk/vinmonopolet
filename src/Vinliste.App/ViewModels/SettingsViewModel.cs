using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vinliste.App.Services;
using Vinliste.Core.Config;
using Vinliste.Core.Models;

namespace Vinliste.App.ViewModels;

public sealed partial class SourceToggle(string id, string label, string description) : ObservableObject
{
    public string Id { get; } = id;
    public string Label { get; } = label;
    public string Description { get; } = description;

    [ObservableProperty] private bool _isEnabled;
}

public sealed partial class SettingsViewModel(AppServices services, Action settingsChanged) : ObservableObject
{
    public ObservableCollection<Store> Stores { get; } = [];

    public ObservableCollection<SourceToggle> Sources { get; } =
    [
        new("vivino", "Vivino", "Community ratings (0–5 stars), per vintage when available."),
        new("cellartracker", "CellarTracker", "Community average of collectors' tasting notes (100 points)."),
        new("falstaff", "Falstaff", "Critic scores from Falstaff's wine guide (100 points)."),
        new("wine_enthusiast", "Wine Enthusiast", "Critic scores from the Wine Enthusiast buying guide (100 points)."),
    ];

    [ObservableProperty] private bool _isDarkTheme;
    [ObservableProperty] private double _matchThreshold = 82;
    [ObservableProperty] private Store? _favoriteStore;
    [ObservableProperty] private string _latitude = "";
    [ObservableProperty] private string _longitude = "";
    [ObservableProperty] private string _savedMessage = "";

    public string DataFolder => AppPaths.DataDirectory;

    public string StoresHint => Stores.Count == 0
        ? "Stores appear here after you check stock for a wine (open a wine and click “Check stock”)."
        : "Your favourite store is listed first in stock lists.";

    private bool _loading;

    public void Load()
    {
        _loading = true;
        try
        {
            LoadCore();
        }
        finally
        {
            _loading = false;
        }
    }

    private void LoadCore()
    {
        var settings = services.Settings;
        IsDarkTheme = settings.Theme == "Dark";
        MatchThreshold = settings.MatchThreshold;
        Latitude = settings.Latitude?.ToString(CultureInfo.InvariantCulture) ?? "";
        Longitude = settings.Longitude?.ToString(CultureInfo.InvariantCulture) ?? "";
        foreach (var source in Sources)
        {
            source.IsEnabled = settings.EnabledRatingSources.Contains(source.Id);
        }
        Stores.Clear();
        foreach (var store in services.Repository.GetStores())
        {
            Stores.Add(store);
        }
        FavoriteStore = Stores.FirstOrDefault(s => s.StoreId == settings.FavoriteStoreId);
        OnPropertyChanged(nameof(StoresHint));
        SavedMessage = "";
    }

    /// <summary>The theme switches and is saved at once, so what you see is what is stored.</summary>
    partial void OnIsDarkThemeChanged(bool value)
    {
        if (_loading)
        {
            return;
        }
        services.Settings.Theme = value ? "Dark" : "Light";
        services.SaveSettings();
        ThemeManager.Apply(services.Settings.Theme);
    }

    partial void OnFavoriteStoreChanged(Store? value)
    {
        // Picking a store uses its position as "my location", so stock lists sort around it.
        // Loading the page must not overwrite a position the user typed in.
        if (!_loading && value?.Latitude is not null && value.Longitude is not null)
        {
            Latitude = value.Latitude.Value.ToString(CultureInfo.InvariantCulture);
            Longitude = value.Longitude.Value.ToString(CultureInfo.InvariantCulture);
        }
    }

    [RelayCommand]
    private void Save()
    {
        var settings = services.Settings;
        settings.Theme = IsDarkTheme ? "Dark" : "Light";
        settings.MatchThreshold = Math.Clamp(Math.Round(MatchThreshold), 60, 100);
        settings.FavoriteStoreId = FavoriteStore?.StoreId;
        settings.FavoriteStoreName = FavoriteStore?.Name;
        settings.Latitude = ParseCoordinate(Latitude);
        settings.Longitude = ParseCoordinate(Longitude);
        settings.EnabledRatingSources = Sources.Where(s => s.IsEnabled).Select(s => s.Id).ToList();
        services.SaveSettings();
        services.RebuildRatings();
        settingsChanged();
        SavedMessage = $"Saved {DateTime.Now:t}";
    }

    [RelayCommand]
    private void OpenDataFolder() =>
        Process.Start(new ProcessStartInfo(AppPaths.DataDirectory) { UseShellExecute = true });

    private static double? ParseCoordinate(string text) =>
        double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
}
