using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Vinliste.App.Services;
using Vinliste.Core.Data;
using Vinliste.Core.Sync;
using Vinliste.Core.Vinmonopolet;

namespace Vinliste.App.ViewModels;

public enum AppPage
{
    AllWines,
    Favorites,
    NewArrivals,
    PriceDrops,
    Settings,
}

public sealed partial class CategoryFilter(string code, string name) : ObservableObject
{
    public string Code { get; } = code;
    public string Name { get; } = name;

    [ObservableProperty] private bool _isSelected;
}

public sealed record SortOption(string Label, WineSort Sort, bool Descending);

public sealed partial class MainViewModel : ObservableObject
{
    private const int PageSize = 3000;
    private const string AllCountries = "All countries";

    private readonly AppServices _services;
    private readonly DispatcherTimer _searchDebounce;
    private bool _suspendRefresh;
    private bool _swappingRows;
    private int _refreshVersion;

    public MainViewModel(AppServices services)
    {
        _services = services;
        Job = new JobViewModel();
        Job.Finished += (_, _) => AfterJob();
        Settings = new SettingsViewModel(services, () => Detail?.Reload());

        foreach (var category in WineCategories.All)
        {
            var filter = new CategoryFilter(category.Code, category.Name);
            filter.PropertyChanged += (_, _) => Refresh();
            Categories.Add(filter);
        }
        SortOptions =
        [
            new("Name, A–Z", WineSort.Name, false),
            new("Name, Z–A", WineSort.Name, true),
            new("Best score", WineSort.BestScore, true),
            new("Value for money", WineSort.ValueForMoney, true),
            new("Price, low to high", WineSort.Price, false),
            new("Price, high to low", WineSort.Price, true),
            new("Litre price", WineSort.LitrePrice, false),
            new("Vintage, oldest first", WineSort.Vintage, false),
            new("Country, A–Z", WineSort.Country, false),
            new("Country, Z–A", WineSort.Country, true),
            new("Litre price, high to low", WineSort.LitrePrice, true),
            new("Best score, low to high", WineSort.BestScore, false),
            new("Newest arrivals", WineSort.FirstSeen, true),
            new("Biggest price drop", WineSort.PriceChange, false),
        ];
        _selectedSort = SortOptions[0];
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            Refresh();
        };
        ThemeManager.ThemeChanged += (_, _) =>
        {
            // Converters pick theme brushes when a binding is evaluated; rebuild rows and the pane.
            Refresh();
            Detail?.Reload();
        };
        LoadCountries();
        UpdateStats();
        Refresh();

        // Background updates report from a worker thread; show them on the UI thread.
        var dispatcher = Dispatcher.CurrentDispatcher;
        services.Background.StatusChanged += (_, status) => dispatcher.BeginInvoke(() =>
        {
            BackgroundText = status.Message;
            IsBackgroundActive = status.IsActive;
            if (status.LastPageAt is not null)
            {
                UpdateStats();
            }
        });
        services.Background.PassCompleted += (_, _) => dispatcher.BeginInvoke(AfterJob);
        if (services.SeedInstalled)
        {
            Job.Message = services.Settings.BackgroundUpdates
                ? "Welcome! The wine list is preinstalled and keeps itself up to date while Vinliste is open."
                : "Welcome! The wine list is preinstalled. Click “Update now” to bring it up to date.";
        }
        _ = services.ApplyBackgroundSettingsAsync();
    }

    [ObservableProperty] private string _backgroundText = "";
    [ObservableProperty] private bool _isBackgroundActive;

    public JobViewModel Job { get; }
    public SettingsViewModel Settings { get; }
    public ObservableCollection<CategoryFilter> Categories { get; } = [];
    public ObservableCollection<string> Countries { get; } = [];
    public IReadOnlyList<SortOption> SortOptions { get; }

    [ObservableProperty] private AppPage _page = AppPage.AllWines;
    [ObservableProperty] private ObservableCollection<WineRow> _wines = [];
    [ObservableProperty] private WineRow? _selectedWine;
    [ObservableProperty] private WineDetailViewModel? _detail;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _selectedCountry = AllCountries;
    [ObservableProperty] private string _minPrice = "";
    [ObservableProperty] private string _maxPrice = "";
    [ObservableProperty] private double _minScore;
    [ObservableProperty] private bool _availableOnly = true;
    [ObservableProperty] private SortOption _selectedSort;
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private string _statsText = "";
    [ObservableProperty] private bool _isCatalogEmpty;
    [ObservableProperty] private string _pageTitle = "All wines";
    [ObservableProperty] private string _pageSubtitle = "";
    [ObservableProperty] private string _emptyListText = "";

    /// <summary>Rows selected in the grid (set from the view; DataGrid.SelectedItems is not bindable).</summary>
    public IReadOnlyList<WineRow> SelectedWines { get; set; } = [];

    public bool IsSettingsPage => Page == AppPage.Settings;

    public string MinScoreText => MinScore <= 0 ? "Any score" : $"{MinScore:0}+ points";

    partial void OnPageChanged(AppPage value)
    {
        OnPropertyChanged(nameof(IsSettingsPage));
        (PageTitle, PageSubtitle) = value switch
        {
            AppPage.Favorites => ("Favourites", "Wines you have marked with a heart."),
            AppPage.NewArrivals => ("New arrivals", "Wines that appeared at Vinmonopolet in the last 30 days."),
            AppPage.PriceDrops => ("Price drops", "Wines that got cheaper at the last catalogue update."),
            AppPage.Settings => ("Settings", ""),
            _ => ("All wines", "Search Vinmonopolet's full wine range with ratings from the web."),
        };
        if (value == AppPage.Settings)
        {
            Settings.Load();
            return;
        }
        _suspendRefresh = true;
        SelectedSort = value switch
        {
            AppPage.NewArrivals => SortOptions.First(o => o.Sort == WineSort.FirstSeen),
            AppPage.PriceDrops => SortOptions.First(o => o.Sort == WineSort.PriceChange),
            // Those pages' sorts make no sense elsewhere; go back to the name order.
            _ when SelectedSort.Sort is WineSort.FirstSeen or WineSort.PriceChange => SortOptions[0],
            _ => SelectedSort,
        };
        _suspendRefresh = false;
        Refresh();
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    partial void OnSelectedCountryChanged(string value) => Refresh();
    partial void OnMinPriceChanged(string value) => Refresh();
    partial void OnMaxPriceChanged(string value) => Refresh();
    partial void OnAvailableOnlyChanged(bool value) => Refresh();
    partial void OnSelectedSortChanged(SortOption value) => Refresh();

    partial void OnMinScoreChanged(double value)
    {
        OnPropertyChanged(nameof(MinScoreText));
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    partial void OnSelectedWineChanged(WineRow? value)
    {
        // The grid clears its selection while rows are swapped; that must not close the
        // detail pane (and cancel a rating in progress).
        if (_swappingRows || (value is not null && Detail?.Row.Code == value.Code))
        {
            return;
        }
        Detail?.Close();
        Detail = value is null ? null : new WineDetailViewModel(_services, value, ReplaceRow);
    }

    /// <summary>Called by the grid when a column header is clicked.</summary>
    public void SortBy(WineSort sort, bool descending) =>
        SelectedSort = SortOptions.First(o => o.Sort == sort && o.Descending == descending);

    public WineQuery BuildQuery(int limit = PageSize)
    {
        var categories = Categories.Where(c => c.IsSelected).Select(c => c.Code).ToList();
        return new WineQuery
        {
            Text = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
            CategoryCodes = categories.Count == 0 ? null : categories,
            Country = SelectedCountry == AllCountries ? null : SelectedCountry,
            MinPrice = ParsePrice(MinPrice),
            MaxPrice = ParsePrice(MaxPrice),
            MinScore = MinScore > 0 ? MinScore : null,
            AvailableOnly = AvailableOnly,
            FavoritesOnly = Page == AppPage.Favorites,
            NewSince = Page == AppPage.NewArrivals ? NewArrivalsSince() : null,
            PriceDroppedOnly = Page == AppPage.PriceDrops,
            Sort = SelectedSort.Sort,
            Descending = SelectedSort.Descending,
            Limit = limit,
        };
    }

    [RelayCommand]
    public void Refresh() => _ = RefreshAsync();

    /// <summary>
    /// Reloads the list off the UI thread. Only the latest request is applied, so typing or
    /// clicking filters quickly never shows an older result.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (_suspendRefresh)
        {
            return;
        }
        var version = ++_refreshVersion;
        var query = BuildQuery();
        var repository = _services.Repository;
        var (rows, total) = await Task.Run(() => (repository.Query(query), repository.Count(query)));
        if (version != _refreshVersion)
        {
            return;
        }

        var selectedCode = Detail?.Row.Code ?? SelectedWine?.Code;
        _swappingRows = true;
        Wines = new ObservableCollection<WineRow>(rows);
        _swappingRows = false;
        ResultText = IsCatalogEmpty ? "" : total == 0 ? "No wines match" : total > Wines.Count
            ? $"Showing {Wines.Count:N0} of {total:N0} wines — narrow the search to see the rest"
            : total == 1 ? "1 wine" : $"{total:N0} wines";
        EmptyListText = IsCatalogEmpty || total > 0 ? "" : Page switch
        {
            AppPage.Favorites => "No favourites yet. Open a wine and click the heart to keep it here.",
            AppPage.NewArrivals => "No new wines yet. Wines that appear in later catalogue updates show up here.",
            AppPage.PriceDrops => "No price drops since the last update. Check back after the next catalogue update.",
            _ => "No wines match these filters.",
        };
        // Re-select the open wine if it is still listed; otherwise the pane stays open on it.
        if (selectedCode is not null && Wines.FirstOrDefault(w => w.Code == selectedCode) is { } again)
        {
            SelectedWine = again;
        }
        ListRefreshed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised after the list is replaced (the grid re-applies its sort arrow).</summary>
    public event EventHandler? ListRefreshed;

    /// <summary>
    /// New arrivals are wines first seen in the last 30 days, but never those from the very
    /// first catalogue run: on a fresh install every wine would otherwise count as new.
    /// </summary>
    private DateTimeOffset NewArrivalsSince()
    {
        var firstRun = _services.Repository.FirstCompletedRunFinishedAt(CatalogSyncService.Kind) ?? DateTimeOffset.UtcNow;
        var monthAgo = DateTimeOffset.UtcNow.AddDays(-30);
        return firstRun > monthAgo ? firstRun : monthAgo;
    }

    [RelayCommand]
    private void ClearFilters()
    {
        _suspendRefresh = true;
        SearchText = "";
        SelectedCountry = AllCountries;
        MinPrice = "";
        MaxPrice = "";
        MinScore = 0;
        foreach (var category in Categories)
        {
            category.IsSelected = false;
        }
        _suspendRefresh = false;
        Refresh();
    }

    [RelayCommand]
    private void UpdateCatalog()
    {
        Job.TryStart("Catalogue update", async (progress, ct) =>
        {
            // The fast update takes over the slow background pass (it continues the same run),
            // and hands back to it afterwards.
            await _services.Background.StopAsync("Paused while “Update now” runs");
            CatalogSyncResult result;
            try
            {
                result = await _services.Catalog.RunAsync(null, progress, ct);
            }
            finally
            {
                if (_services.Settings.BackgroundUpdates)
                {
                    _services.Background.Start();
                }
            }
            return result.Status switch
            {
                SyncRunStatus.Completed => $"Catalogue updated · {result.ProductsSeen:N0} wines · {result.ProductsNew:N0} new · {result.PriceChanges:N0} price changes · {result.Delisted:N0} gone",
                SyncRunStatus.Cancelled => $"Update paused after {result.ProductsSeen:N0} wines. Click Update again to continue where it stopped.",
                _ => $"Update stopped: {result.Error}. Click Update to retry; it continues where it stopped.",
            };
        });
    }

    [RelayCommand]
    private void FetchDetails()
    {
        var codes = _services.Repository.Query(BuildQuery(limit: 1000)).Select(w => w.Code).ToList();
        Job.TryStart("Wine details", async (progress, ct) =>
        {
            var saved = await _services.Details.RunAsync(codes, codes.Count, progress, ct);
            return $"Fetched tasting notes and details for {saved:N0} wines.";
        });
    }

    [RelayCommand]
    private void RateSelected()
    {
        var codes = SelectedWines.Select(w => w.Code).ToList();
        if (codes.Count == 0 && SelectedWine is not null)
        {
            codes.Add(SelectedWine.Code);
        }
        if (codes.Count == 0)
        {
            Job.Message = "Select one or more wines first (Ctrl- or Shift-click to select several).";
            return;
        }
        StartRating(codes, missingOnly: false, "Ratings for selected wines");
    }

    [RelayCommand]
    private void RateFiltered()
    {
        var codes = _services.Repository.Query(BuildQuery(limit: 500)).Select(w => w.Code).ToList();
        StartRating(codes, missingOnly: true, "Ratings for listed wines");
    }

    private void StartRating(IReadOnlyList<string> codes, bool missingOnly, string title)
    {
        if (codes.Count == 0)
        {
            return;
        }
        Job.TryStart(title, async (progress, ct) =>
        {
            // Ratings match better with the producer and vintage from the product page.
            await _services.Details.RunAsync(codes, codes.Count, null, ct);
            var matched = await _services.Ratings.RunAsync(codes, missingOnly, progress, ct);
            return $"Ratings done · {matched:N0} of {codes.Count:N0} wines have at least one score.";
        });
    }

    [RelayCommand]
    private void Export()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export wine list",
            FileName = $"vinliste-{DateTime.Now:yyyy-MM-dd}",
            Filter = "Excel-friendly CSV (*.csv)|*.csv|JSON (*.json)|*.json",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        var wines = _services.Repository.Query(BuildQuery(limit: int.MaxValue));
        try
        {
            Exporter.Write(dialog.FileName, wines);
            Job.Message = $"Exported {wines.Count:N0} wines to {dialog.FileName}";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Job.Message = $"Could not write {dialog.FileName}: {ex.Message} (is it open in Excel?)";
        }
    }

    /// <summary>Updates one row in place (keeps scroll position and the open detail pane).</summary>
    private void ReplaceRow(WineRow row)
    {
        var index = Wines.ToList().FindIndex(w => w.Code == row.Code);
        if (index >= 0)
        {
            var wasSelected = SelectedWine?.Code == row.Code;
            _swappingRows = true;
            Wines[index] = row;
            _swappingRows = false;
            if (wasSelected)
            {
                SelectedWine = row;
            }
        }
        UpdateStats();
    }

    private void AfterJob()
    {
        LoadCountries();
        UpdateStats();
        Refresh();
        Detail?.Reload();
    }

    private void LoadCountries()
    {
        // Clearing makes the ComboBox push a null selection; none of that may trigger reloads.
        var current = SelectedCountry;
        _suspendRefresh = true;
        Countries.Clear();
        Countries.Add(AllCountries);
        foreach (var country in _services.Repository.GetCountries())
        {
            Countries.Add(country);
        }
        SelectedCountry = Countries.Contains(current) ? current : AllCountries;
        _suspendRefresh = false;
    }

    private void UpdateStats()
    {
        var stats = _services.Repository.GetStats();
        IsCatalogEmpty = stats.Total == 0;
        StatsText = stats.Total == 0
            ? "Catalogue is empty"
            : $"{stats.Active:N0} wines · {stats.WithRatings:N0} rated\n{stats.WithDetails:N0} with details"
              + (stats.LastSync is null ? "" : $"\nFull pass {stats.LastSync.Value.LocalDateTime:d}");
    }

    private static double? ParsePrice(string text) =>
        double.TryParse(text.Replace(" ", "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value
            : null;
}
