using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vinliste.App.Services;
using Vinliste.Core.Data;
using Vinliste.Core.Models;
using Vinliste.Core.Vinmonopolet;

namespace Vinliste.App.ViewModels;

public sealed record RatingDisplay(
    string Source,
    string Label,
    string Status,
    string ScoreText,
    double? Score100,
    string? Url,
    string? MatchedName,
    string Detail);

public sealed record StockDisplay(string Store, string Address, string? Distance, int StockLevel, bool IsFavorite);

public sealed record CharacteristicDisplay(string Name, int Value);

/// <summary>The detail pane for one wine. Loads stored data at once and refreshes from the web in the background.</summary>
public sealed partial class WineDetailViewModel : ObservableObject
{
    private static readonly Dictionary<string, string> SourceLabels = new()
    {
        ["vivino"] = "Vivino",
        ["cellartracker"] = "CellarTracker",
        ["falstaff"] = "Falstaff",
        ["wine_enthusiast"] = "Wine Enthusiast",
    };

    private readonly AppServices _services;
    private readonly Action<WineRow> _rowChanged;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _loadingNote;

    public WineDetailViewModel(AppServices services, WineRow row, Action<WineRow> rowChanged)
    {
        _services = services;
        _rowChanged = rowChanged;
        _row = row;
        _loadingNote = true;
        Note = row.Note ?? "";
        IsFavorite = row.IsFavorite;
        _loadingNote = false;
        LoadStored();
        _ = RefreshDetailsIfStaleAsync();
    }

    [ObservableProperty] private WineRow _row;
    [ObservableProperty] private WineDetails? _details;
    [ObservableProperty] private bool _isLoadingDetails;
    [ObservableProperty] private bool _isLoadingStock;
    [ObservableProperty] private bool _isRating;
    [ObservableProperty] private string _stockStatus = "";
    [ObservableProperty] private string _detailsStatus = "";
    [ObservableProperty] private string _ratingMessage = "";
    [ObservableProperty] private string _note;
    [ObservableProperty] private bool _isFavorite;

    public ObservableCollection<RatingDisplay> Ratings { get; } = [];
    public ObservableCollection<StockDisplay> Stock { get; } = [];
    public ObservableCollection<CharacteristicDisplay> Characteristics { get; } = [];
    public ObservableCollection<PricePoint> PriceHistory { get; } = [];

    public string Subtitle => string.Join(" · ", new[]
        {
            Row.Producer,
            WineCategories.All.FirstOrDefault(c => c.Code == Row.CategoryCode)?.Name ?? Row.Category,
            JoinNonEmpty(", ", Row.District, Row.Country),
        }
        .Where(s => !string.IsNullOrWhiteSpace(s)));

    public string PriceText => Row.Price is null ? "–" : $"kr {Row.Price:N2}";

    public string FactsText => JoinNonEmpty(" · ",
        Row.VolumeCl is null ? null : $"{Row.VolumeCl:0.#} cl",
        Row.Alcohol is null ? null : $"{Row.Alcohol:0.#} %",
        Row.LitrePrice is null ? null : $"kr {Row.LitrePrice:N0}/l",
        Row.ProductSelection);

    public string PriceChangeText => Row.PriceChange switch
    {
        null or 0 => "",
        < 0 => $"▼ kr {-Row.PriceChange:N0} since last update",
        _ => $"▲ kr {Row.PriceChange:N0} since last update",
    };

    public string BestScoreText => Row.BestScore is null ? "–" : $"{Row.BestScore:0.#}";

    public string BestScoreCaption => Row.RatingSources switch
    {
        0 => "No ratings yet",
        1 => "from 1 source",
        var n => $"average of {n} sources",
    };

    public string GrapesText => Details is null || Details.Grapes.Count == 0
        ? ""
        : string.Join(", ", Details.Grapes.Select(g => g.Percent is null ? g.Name : $"{g.Name} {g.Percent:0}%"));

    public IReadOnlyList<string> Food => Details?.FoodPairings.Select(f => f.Name).ToList() ?? [];

    public IReadOnlyList<string> Tags => new[]
    {
        Details?.Eco == true ? "Organic" : null,
        Details?.Biodynamic == true ? "Biodynamic" : null,
        Details?.Vegan == true ? "Vegan" : null,
        Details?.FairTrade == true ? "Fair trade" : null,
        Details?.Kosher == true ? "Kosher" : null,
        Details?.EnvironmentalPackaging == true ? "Climate-smart packaging" : null,
    }.OfType<string>().ToList();

    public bool HasTastingNotes => Details is not null && (Details.Colour ?? Details.Smell ?? Details.Taste) is not null;

    public string DetailsUpdatedText => Row.DetailsUpdatedAt is null ? "Details not fetched yet" : $"Details from {Row.DetailsUpdatedAt.Value.LocalDateTime:d}";

    partial void OnDetailsChanged(WineDetails? value)
    {
        Characteristics.Clear();
        if (value is not null)
        {
            foreach (var (name, number) in new[]
                     {
                         ("Fullness", value.Fullness), ("Freshness", value.Freshness), ("Tannins", value.Tannins),
                         ("Sweetness", value.Sweetness), ("Bitterness", value.Bitterness),
                     })
            {
                if (number is not null)
                {
                    Characteristics.Add(new CharacteristicDisplay(name, number.Value));
                }
            }
        }
        OnPropertyChanged(nameof(GrapesText));
        OnPropertyChanged(nameof(Food));
        OnPropertyChanged(nameof(Tags));
        OnPropertyChanged(nameof(HasTastingNotes));
    }

    partial void OnRowChanged(WineRow value)
    {
        foreach (var name in new[] { nameof(Subtitle), nameof(PriceText), nameof(FactsText), nameof(PriceChangeText), nameof(BestScoreText), nameof(BestScoreCaption), nameof(DetailsUpdatedText) })
        {
            OnPropertyChanged(name);
        }
    }

    partial void OnIsFavoriteChanged(bool value)
    {
        if (_loadingNote || value == Row.IsFavorite)
        {
            return;
        }
        _services.Repository.SetFavorite(Row.Code, value);
        ReloadRow();
    }

    [RelayCommand]
    private void SaveNote()
    {
        if ((Row.Note ?? "") == Note.Trim())
        {
            return;
        }
        _services.Repository.SetNote(Row.Code, Note);
        ReloadRow();
    }

    [RelayCommand]
    private void OpenVinmonopolet() => OpenUrl(Row.Url);

    [RelayCommand]
    private void OpenUrl(string? url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
    }

    [RelayCommand]
    private async Task RefreshDetailsAsync()
    {
        IsLoadingDetails = true;
        try
        {
            var details = await Task.Run(() => _services.Details.RefreshAsync(Row.Code, _lifetime.Token));
            if (details is not null)
            {
                Details = details;
            }
            ReloadRow();
        }
        catch (OperationCanceledException)
        {
            // The pane was closed (another wine selected); nothing to report.
        }
        catch (Exception ex)
        {
            DetailsStatus = $"Could not fetch details: {ex.Message}";
        }
        finally
        {
            IsLoadingDetails = false;
        }
    }

    [RelayCommand]
    private async Task RefreshStockAsync()
    {
        IsLoadingStock = true;
        StockStatus = "Checking stores…";
        try
        {
            var settings = _services.Settings;
            await Task.Run(() => _services.Stock.RefreshAsync(Row.Code, settings.Latitude, settings.Longitude, _lifetime.Token));
            LoadStock();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StockStatus = $"Could not fetch stock: {ex.Message}";
        }
        finally
        {
            IsLoadingStock = false;
        }
    }

    [RelayCommand]
    private async Task RateAsync()
    {
        IsRating = true;
        RatingMessage = "Searching rating sites… this takes up to a minute.";
        try
        {
            await Task.Run(() => _services.Ratings.RateAsync(Row.Code, force: true, _lifetime.Token));
            LoadRatings();
            ReloadRow();
            RatingMessage = "";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RatingMessage = $"Rating failed: {ex.Message}";
        }
        finally
        {
            IsRating = false;
        }
    }

    /// <summary>Called when the pane is replaced or the window closes: keeps the note, stops lookups.</summary>
    public void Close()
    {
        SaveNote();
        _lifetime.Cancel();
    }

    /// <summary>Reloads everything from the database (after a background job changed it).</summary>
    public void Reload()
    {
        ReloadRow();
        LoadStored();
    }

    private void LoadStored()
    {
        Details = _services.Repository.GetDetails(Row.Code);
        LoadRatings();
        LoadStock();
        PriceHistory.Clear();
        foreach (var point in _services.Repository.GetPriceHistory(Row.Code))
        {
            PriceHistory.Add(point);
        }
    }

    private void LoadRatings()
    {
        Ratings.Clear();
        var stored = _services.Repository.GetRatings(Row.Code).ToDictionary(r => r.Source);
        foreach (var source in _services.Ratings.Sources)
        {
            Ratings.Add(stored.TryGetValue(source.Id, out var rating)
                ? ToDisplay(rating)
                : new RatingDisplay(source.Id, source.Label, "Not checked", "–", null, null, null, "Click “Find ratings” to search."));
        }
        RatingMessage = stored.Count == 0 && !IsRating ? "No ratings collected yet." : "";
    }

    private void LoadStock()
    {
        Stock.Clear();
        var favorite = _services.Settings.FavoriteStoreId;
        var stock = _services.Repository.GetStock(Row.Code)
            .OrderByDescending(s => s.Store.StoreId == favorite)
            .ToList();
        foreach (var entry in stock)
        {
            Stock.Add(new StockDisplay(
                entry.Store.Name,
                JoinNonEmpty(", ", entry.Store.Address, entry.Store.Town),
                entry.Distance,
                entry.StockLevel,
                entry.Store.StoreId == favorite));
        }
        StockStatus = stock.Count == 0
            ? "Stock not checked yet."
            : $"{stock.Sum(s => s.StockLevel):N0} bottles in {stock.Count} {(stock.Count == 1 ? "store" : "stores")} · checked {stock[0].ObservedAt.LocalDateTime:g}";
    }

    private void ReloadRow()
    {
        if (_services.Repository.GetWine(Row.Code) is { } fresh)
        {
            Row = fresh;
            _rowChanged(fresh);
        }
    }

    private async Task RefreshDetailsIfStaleAsync()
    {
        if (Row.DetailsUpdatedAt is { } updated && DateTimeOffset.UtcNow - updated < TimeSpan.FromDays(30))
        {
            return;
        }
        await RefreshDetailsAsync();
    }

    private static RatingDisplay ToDisplay(RatingResult r)
    {
        var label = SourceLabels.GetValueOrDefault(r.Source, r.Source);
        var status = r.Status switch
        {
            RatingStatus.Matched => "Matched",
            RatingStatus.MatchedNoScore => "Found, no score yet",
            RatingStatus.NoMatch => "Not found",
            RatingStatus.Blocked => "Site blocked the lookup",
            _ => "Lookup failed",
        };
        var score = r.Score is null ? "–" : r.Source == "vivino" ? $"{r.Score:0.0} ★" : $"{r.Score:0.#} pts";
        var detail = r.Status switch
        {
            RatingStatus.Matched or RatingStatus.MatchedNoScore => JoinNonEmpty(" · ",
                r.MatchedVintage is null && r.Source == "vivino" ? "all vintages" : r.MatchedVintage,
                r.RatingCount is null ? null : $"{r.RatingCount:N0} ratings",
                r.MatchConfidence is null ? null : $"{r.MatchConfidence:0}% match"),
            RatingStatus.NoMatch => r.MatchConfidence is null ? "No similar wine found" : $"Closest candidate {r.MatchConfidence:0}% match",
            _ => r.Error ?? "",
        } + $" · {r.FetchedAt.LocalDateTime:d}";
        return new RatingDisplay(r.Source, label, status, score, r.Score100, r.Url, r.MatchedName, detail);
    }

    private static string JoinNonEmpty(string separator, params string?[] parts) =>
        string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}
