using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Vinliste.App.Services;

/// <summary>Swaps the light/dark palette and paints the Windows title bar to match.</summary>
public static class ThemeManager
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;

    public static string Current { get; private set; } = "Light";

    /// <summary>Raised after a theme switch so views can rebuild converter-made brushes.</summary>
    public static event EventHandler? ThemeChanged;

    public static void Apply(string theme)
    {
        var next = theme == "Dark" ? "Dark" : "Light";
        var changed = next != Current;
        Current = next;
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var palette = new ResourceDictionary { Source = new Uri($"Themes/{Current}.xaml", UriKind.Relative) };
        // The palette is always the first merged dictionary (see App.xaml).
        dictionaries[0] = palette;
        foreach (Window window in Application.Current.Windows)
        {
            ApplyTitleBar(window);
        }
        if (changed)
        {
            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    public static void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }
        var dark = 1;
        DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
        if (Application.Current.TryFindResource("CaptionColor") is Color caption)
        {
            var colorRef = caption.R | (caption.G << 8) | (caption.B << 16);
            DwmSetWindowAttribute(handle, DwmCaptionColor, ref colorRef, sizeof(int));
        }
        if (Application.Current.TryFindResource("CaptionTextColor") is Color text)
        {
            var colorRef = text.R | (text.G << 8) | (text.B << 16);
            DwmSetWindowAttribute(handle, DwmTextColor, ref colorRef, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
