using System.IO;
using DotDeck.App.Infrastructure.Windows;

namespace DotDeck.App.Tests;

public sealed class UninstallCleanupTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "dotdeck-uninstall-" + Guid.NewGuid().ToString("N"));
    private readonly string _startup;
    private readonly string _local;

    public UninstallCleanupTests()
    {
        _startup = Directory.CreateDirectory(Path.Combine(_temp, "Startup")).FullName;
        _local = Directory.CreateDirectory(Path.Combine(_temp, "Local", "DotDeck")).FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private string Touch(params string[] parts)
    {
        var path = Path.Combine(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void Removes_autostart_shortcut_and_update_cache_but_keeps_user_data()
    {
        var shortcut = Touch(_startup, UninstallCleanup.StartupShortcutName);
        var otherShortcut = Touch(_startup, "Other.lnk");
        Touch(_local, "packages", "DotDeck-1.6.0-full.nupkg");
        var updateExe = Touch(_local, "Update.exe");
        var backup = Touch(_local, "backups", "settings.json.bak");
        var state = Touch(_local, "state", "session.json");
        var log = Touch(_local, "logs", "dotdeck.log");

        UninstallCleanup.Run(_startup, _local);

        Assert.False(File.Exists(shortcut));
        Assert.False(Directory.Exists(Path.Combine(_local, "packages")));
        Assert.False(File.Exists(updateExe));
        Assert.True(File.Exists(otherShortcut));
        Assert.True(File.Exists(backup));
        Assert.True(File.Exists(state));
        Assert.True(File.Exists(log));
    }

    [Fact]
    public void Leaves_a_per_user_install_root_to_its_own_uninstaller()
    {
        // A pre-1.6 per-user install living in %LOCALAPPDATA%\DotDeck: its packages are not a fallback cache.
        Touch(_local, "current", "DotDeck.exe");
        var package = Touch(_local, "packages", "DotDeck-1.5.0-full.nupkg");
        var updateExe = Touch(_local, "Update.exe");

        UninstallCleanup.Run(_startup, _local);

        Assert.True(File.Exists(package));
        Assert.True(File.Exists(updateExe));
    }

    [Fact]
    public void Missing_folders_are_not_an_error()
    {
        UninstallCleanup.Run(Path.Combine(_temp, "none"), Path.Combine(_temp, "none2"));
    }
}
