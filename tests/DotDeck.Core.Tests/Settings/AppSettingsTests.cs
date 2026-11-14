using DotDeck.Core.Files;
using DotDeck.Core.Settings;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.Settings;

public sealed class AppSettingsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Round_trips_and_defaults_when_missing_or_broken()
    {
        var path = _temp.Combine("cfg", "settings.json");
        var store = new AppSettingsStore(PhysicalFileStore.Instance, path);
        Assert.Equal(new AppSettings(), store.Load());

        store.Save(new AppSettings { Theme = "Light", EditorFontSize = 15, BackupRetention = 10 });
        Assert.Equal(new AppSettings { Theme = "Light", EditorFontSize = 15, BackupRetention = 10 }, store.Load());

        File.WriteAllText(path, "{ broken");
        Assert.Equal(new AppSettings(), store.Load());
    }

    [Fact]
    public void Close_to_tray_is_on_by_default_and_round_trips()
    {
        var path = _temp.Combine("tray-settings.json");
        var store = new AppSettingsStore(PhysicalFileStore.Instance, path);
        Assert.True(store.Load().CloseToTray);

        File.WriteAllText(path, "{ \"Theme\": \"Dark\" }");
        Assert.True(store.Load().CloseToTray);

        store.Save(new AppSettings { CloseToTray = false });
        Assert.False(store.Load().CloseToTray);
    }

    [Fact]
    public void Screen_share_mode_is_off_by_default_round_trips_and_ignores_the_old_mask_setting()
    {
        var path = _temp.Combine("settings.json");
        var store = new AppSettingsStore(PhysicalFileStore.Instance, path);
        Assert.False(new AppSettings().ScreenShareMode);

        File.WriteAllText(path, "{ \"MaskSecretsByDefault\": true }");
        Assert.False(store.Load().ScreenShareMode);

        store.Save(new AppSettings { ScreenShareMode = true });
        Assert.True(store.Load().ScreenShareMode);
    }

    [Fact]
    public void Normalize_clamps_bad_values()
    {
        var s = new AppSettings { Theme = "Neon", EditorFontSize = 200, BackupRetention = 0 }.Normalize();

        Assert.Equal(("Dark", 32.0, 1), (s.Theme, s.EditorFontSize, s.BackupRetention));
    }
}
