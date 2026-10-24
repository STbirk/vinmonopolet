using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Vinliste.App.Services;
using Vinliste.App.ViewModels;
using Vinliste.Core.Data;

namespace Vinliste.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        FocusSearchCommand = new RelayCommand(() =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        });
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        ContentRendered += async (_, _) => await SnapshotForTestingAsync();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is MainViewModel model)
            {
                model.ListRefreshed += (_, _) => ShowSortArrow();
            }
        };
        // Keep a note typed just before closing.
        Closing += (_, _) => ViewModel.Detail?.Close();
    }

    private DataGridColumn? _sortColumn;
    private bool _sortDescending;

    /// <summary>The grid forgets column sort arrows whenever its rows are replaced; put it back.</summary>
    private void ShowSortArrow()
    {
        foreach (var column in WineGrid.Columns)
        {
            column.SortDirection = column == _sortColumn
                ? (_sortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending)
                : null;
        }
    }

    /// <summary>
    /// Test hook: with VINLISTE_SNAPSHOT=path set, renders the window to a PNG after
    /// VINLISTE_SNAPSHOT_DELAY seconds (default 3). VINLISTE_SNAPSHOT_SELECT=first selects
    /// the first wine, VINLISTE_SNAPSHOT_PAGE picks a page, VINLISTE_SNAPSHOT_EXIT=1 closes afterwards.
    /// Screen capture does not work on a locked or remote desktop; this does.
    /// </summary>
    private async Task SnapshotForTestingAsync()
    {
        var path = Environment.GetEnvironmentVariable("VINLISTE_SNAPSHOT");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        if (Enum.TryParse<AppPage>(Environment.GetEnvironmentVariable("VINLISTE_SNAPSHOT_PAGE"), out var page))
        {
            ViewModel.Page = page;
        }
        await ViewModel.RefreshAsync();
        if (Environment.GetEnvironmentVariable("VINLISTE_SNAPSHOT_SEARCH") is { Length: > 0 } search)
        {
            ViewModel.SearchText = search;
            await ViewModel.RefreshAsync();
        }
        if (Environment.GetEnvironmentVariable("VINLISTE_SNAPSHOT_SELECT") == "first" && ViewModel.Wines.Count > 0)
        {
            ViewModel.SelectedWine = ViewModel.Wines[0];
        }
        if (Environment.GetEnvironmentVariable("VINLISTE_SNAPSHOT_RATE") == "1" && ViewModel.Detail is { } detail)
        {
            detail.RateCommand.Execute(null);
        }
        var delay = double.TryParse(Environment.GetEnvironmentVariable("VINLISTE_SNAPSHOT_DELAY"), out var seconds) ? seconds : 3;
        await Task.Delay(TimeSpan.FromSeconds(delay));

        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var content = (FrameworkElement)Content;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var stream = System.IO.File.Create(path))
        {
            encoder.Save(stream);
        }
        if (Environment.GetEnvironmentVariable("VINLISTE_SNAPSHOT_EXIT") == "1")
        {
            Close();
        }
    }

    public ICommand FocusSearchCommand { get; }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    /// <summary>Header clicks sort in the database (the grid only holds one page of rows).</summary>
    private void OnGridSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var sort = e.Column.SortMemberPath switch
        {
            "Price" => WineSort.Price,
            "LitrePrice" => WineSort.LitrePrice,
            "BestScore" => WineSort.BestScore,
            "Country" => WineSort.Country,
            _ => WineSort.Name,
        };
        // A second click on the same header reverses it. Scores read best high-to-low first;
        // everything else low-to-high first.
        _sortDescending = e.Column == _sortColumn ? !_sortDescending : sort == WineSort.BestScore;
        _sortColumn = e.Column;
        ShowSortArrow();
        ViewModel.SortBy(sort, _sortDescending);
    }

    private void OnGridSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.SelectedWines = WineGrid.SelectedItems.OfType<WineRow>().ToList();
}
