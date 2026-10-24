using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Vinliste.Core.Models;

namespace Vinliste.App.Controls;

/// <summary>A small price-history step line over time, with the current price labelled.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IEnumerable), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnPointsChanged));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.DarkRed, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Points
    {
        get => (IEnumerable?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Brush LabelBrush
    {
        get => (Brush)GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    private static void OnPointsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var sparkline = (Sparkline)d;
        if (e.OldValue is INotifyCollectionChanged oldCollection)
        {
            oldCollection.CollectionChanged -= sparkline.OnCollectionChanged;
        }
        if (e.NewValue is INotifyCollectionChanged newCollection)
        {
            newCollection.CollectionChanged += sparkline.OnCollectionChanged;
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        var points = Points?.OfType<PricePoint>().OrderBy(p => p.ObservedAt).ToList() ?? [];
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface("Segoe UI");
        if (points.Count == 0)
        {
            return;
        }
        if (points.Count == 1)
        {
            var text = new FormattedText($"Price unchanged since first seen ({points[0].ObservedAt.LocalDateTime:d})",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 11, LabelBrush, dpi);
            dc.DrawText(text, new Point(0, Math.Max(0, (ActualHeight - text.Height) / 2)));
            return;
        }

        const double labelWidth = 70;
        var width = Math.Max(10, ActualWidth - labelWidth);
        var height = ActualHeight - 6;
        var min = points.Min(p => p.Price);
        var max = points.Max(p => p.Price);
        var span = Math.Max(1, max - min);
        // Time axis: a price that held for a year takes more room than one that held a week.
        var start = points[0].ObservedAt;
        var duration = Math.Max(1, (DateTimeOffset.UtcNow - start).TotalSeconds);
        double Y(double price) => 3 + height - (price - min) / span * height;
        double X(int index) => (points[index].ObservedAt - start).TotalSeconds / duration * width;

        // Step line: a price holds until the next change.
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(X(0), Y(points[0].Price)), false, false);
            for (var i = 1; i < points.Count; i++)
            {
                context.LineTo(new Point(X(i), Y(points[i - 1].Price)), true, true);
                context.LineTo(new Point(X(i), Y(points[i].Price)), true, true);
            }
            // The latest price holds until today.
            context.LineTo(new Point(width, Y(points[^1].Price)), true, true);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, new Pen(Stroke, 2) { LineJoin = PenLineJoin.Round }, geometry);
        dc.DrawEllipse(Stroke, null, new Point(width, Y(points[^1].Price)), 3.5, 3.5);

        var last = new FormattedText($"kr {points[^1].Price:N0}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 11, LabelBrush, dpi);
        dc.DrawText(last, new Point(width + 8, Math.Clamp(Y(points[^1].Price) - last.Height / 2, 0, Math.Max(0, ActualHeight - last.Height))));
    }
}
