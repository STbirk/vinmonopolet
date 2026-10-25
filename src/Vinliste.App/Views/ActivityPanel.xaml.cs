using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Vinliste.App.ViewModels;

namespace Vinliste.App.Views;

public partial class ActivityPanel : UserControl
{
    public ActivityPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ActivityViewModel old)
            {
                old.LineAdded -= OnLineAdded;
            }
            if (e.NewValue is ActivityViewModel model)
            {
                model.LineAdded += OnLineAdded;
            }
        };
        IsVisibleChanged += (_, _) => ScrollToEnd(force: true);
    }

    private void OnLineAdded(object? sender, EventArgs e) => ScrollToEnd(force: false);

    /// <summary>Follows new lines like a terminal, unless the user has scrolled up to read.</summary>
    private void ScrollToEnd(bool force)
    {
        if (!IsVisible || LineList.Items.Count == 0)
        {
            return;
        }
        var viewer = FindScrollViewer(LineList);
        var atEnd = viewer is null || viewer.VerticalOffset >= viewer.ScrollableHeight - 2;
        if (force || atEnd)
        {
            LineList.ScrollIntoView(LineList.Items[^1]);
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer)
            {
                return viewer;
            }
            if (FindScrollViewer(child) is { } nested)
            {
                return nested;
            }
        }
        return null;
    }
}
