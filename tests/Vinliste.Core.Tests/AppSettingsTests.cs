using Vinliste.Core.Config;

namespace Vinliste.Core.Tests;

public class AppSettingsTests
{
    [Fact]
    public void Settings_round_trip()
    {
        var path = Path.Combine(Path.GetTempPath(), "vinliste-tests", Guid.NewGuid().ToString("N"), "settings.json");
        var settings = new AppSettings { Theme = "Dark", FavoriteStoreId = "178", MatchThreshold = 85, EnabledRatingSources = ["vivino"] };

        settings.Save(path);
        var loaded = AppSettings.Load(path);

        Assert.Equal("Dark", loaded.Theme);
        Assert.Equal("178", loaded.FavoriteStoreId);
        Assert.Equal(85, loaded.MatchThreshold);
        Assert.Equal(["vivino"], loaded.EnabledRatingSources);
    }

    [Fact]
    public void Broken_settings_file_falls_back_to_defaults_and_keeps_a_copy()
    {
        var path = Path.Combine(Path.GetTempPath(), "vinliste-tests", Guid.NewGuid().ToString("N"), "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");

        var settings = AppSettings.Load(path);

        Assert.Equal(82, settings.MatchThreshold);
        Assert.Equal("{ not json", File.ReadAllText(path + ".broken"));
    }
}
