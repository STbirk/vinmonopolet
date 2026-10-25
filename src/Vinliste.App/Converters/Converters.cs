using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Vinliste.App.Converters;

/// <summary>Visible when the value is truthy (non-null, non-empty, true, non-zero); Invert flips it.</summary>
public sealed class VisibleWhenConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var truthy = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            int i => i != 0,
            double d => d != 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };
        return truthy ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Score on 0–100 to the badge colour.</summary>
public sealed class ScoreBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // 88+ is excellent for critics and ~4.4 stars on Vivino; 80 is a solid 4.0.
        var key = value switch
        {
            double s when s >= 88 => "ScoreHighBrush",
            double s when s >= 80 => "ScoreMidBrush",
            double => "ScoreLowBrush",
            _ => "SurfaceAltBrush",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A wine type's colour for the dot in the list.</summary>
public sealed class CategoryBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, Brush> Brushes = new()
    {
        ["rødvin"] = Make("#7A1F33"),
        ["hvitvin"] = Make("#E3C76A"),
        ["rosévin"] = Make("#EBA1AE"),
        ["musserende_vin"] = Make("#F1DE9B"),
        ["perlende_vin"] = Make("#D8E3A0"),
        ["sterkvin"] = Make("#8A4B26"),
        ["aromatisert_vin"] = Make("#A8577A"),
        ["fruktvin"] = Make("#C9603C"),
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string code && Brushes.TryGetValue(code, out var brush) ? brush : System.Windows.Media.Brushes.Gray;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    private static Brush Make(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Price change to green (cheaper) or red (dearer).</summary>
public sealed class PriceChangeBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Application.Current.TryFindResource(value is double d && d > 0 ? "DangerBrush" : "SuccessBrush") as Brush ?? Brushes.Gray;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Formats a number with a format string given as the parameter; null shows a dash.</summary>
public sealed class NumberConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? "–" : string.Format(culture, (string?)parameter ?? "{0}", value);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Stock level to a short label and colour hint.</summary>
public sealed class StockTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        int n when n <= 0 => "Sold out",
        int n => $"{n} pcs",
        _ => "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>"Producer · 2021 · 75 cl" for a list row, skipping whatever is unknown.</summary>
public sealed class WineSubtitleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Vinliste.Core.Data.WineRow row
            ? string.Join(" · ", new[]
                {
                    row.Producer,
                    row.Vintage,
                    row.VolumeCl is null ? null : string.Format(culture, "{0:0.#} cl", row.VolumeCl),
                }.Where(p => !string.IsNullOrWhiteSpace(p)))
            : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Full opacity while active, dimmed when idle or off.</summary>
public sealed class ActiveOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? 1.0 : 0.4;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Enum page to IsChecked for the navigation rail.</summary>
public sealed class EnumMatchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string name ? Enum.Parse(targetType, name) : Binding.DoNothing;
}
