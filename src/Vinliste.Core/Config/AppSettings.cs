using System.Text.Json;

namespace Vinliste.Core.Config;

public static class AppPaths
{
    /// <summary>%LOCALAPPDATA%\Vinliste, or VINLISTE_DATA_DIR when set (used for testing with a separate database).</summary>
    public static string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("VINLISTE_DATA_DIR") is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vinliste");

    public static string DatabasePath => Path.Combine(DataDirectory, "vinliste.db");

    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public static string WebViewDataDirectory => Path.Combine(DataDirectory, "WebView2");
}

/// <summary>User settings in %LOCALAPPDATA%\Vinliste\settings.json. Never committed.</summary>
public sealed class AppSettings
{
    public string? FavoriteStoreId { get; set; }
    public string? FavoriteStoreName { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public double MatchThreshold { get; set; } = 82;
    public int RequestDelayMs { get; set; } = 300;
    public string Theme { get; set; } = "Light";

    /// <summary>Keep the catalogue up to date slowly while the app is open.</summary>
    public bool BackgroundUpdates { get; set; } = true;

    /// <summary>Seconds between listing pages for background updates (one page per minute by default).</summary>
    public int BackgroundPageDelaySeconds { get; set; } = 60;

    /// <summary>Collect ratings slowly while the app is open.</summary>
    public bool BackgroundRatings { get; set; } = true;

    /// <summary>Seconds between wines for background ratings (one wine every five minutes by default).</summary>
    public int BackgroundRatingDelaySeconds { get; set; } = 300;

    /// <summary>Whether the activity panel (the mini terminal) is open.</summary>
    public bool ShowActivityPanel { get; set; }

    /// <summary>Whether the activity panel also lists every web request.</summary>
    public bool ShowWebRequests { get; set; }
    public List<string> EnabledRatingSources { get; set; } = ["vivino", "cellartracker", "falstaff", "wine_enthusiast"];

    public static AppSettings Load(string? path = null)
    {
        path ??= AppPaths.SettingsPath;
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A broken settings file must not stop the app. Keep a copy so the next Save
            // does not destroy the user's edits, then fall back to defaults.
            try
            {
                File.Copy(path, path + ".broken", overwrite: true);
            }
            catch (Exception copyError) when (copyError is IOException or UnauthorizedAccessException)
            {
            }
        }
        return new AppSettings();
    }

    public void Save(string? path = null)
    {
        path ??= AppPaths.SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }
}
