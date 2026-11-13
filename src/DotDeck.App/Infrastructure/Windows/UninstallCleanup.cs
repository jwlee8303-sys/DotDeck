using System.IO;

namespace DotDeck.App.Infrastructure.Windows;

/// <summary>
/// Velopack's uninstall hook (also run when the MSI removes the previous version during an upgrade; the setup then
/// re-applies the user's autostart choice). Removes what the installer and updater put in the user profile — the
/// "run at sign-in" shortcut and the update cache — and never touches settings or user data in the same folders.
/// </summary>
public static class UninstallCleanup
{
    /// <summary>The shortcut DotDeck-Setup.exe creates in the current user's Startup folder.</summary>
    public const string StartupShortcutName = "DotDeck.lnk";

    public static void Run() => Run(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DotDeck"));

    /// <param name="startupFolder">The user's Startup folder.</param>
    /// <param name="localAppFolder">%LOCALAPPDATA%\DotDeck: app data, plus the update cache of a machine-wide install.</param>
    public static void Run(string startupFolder, string localAppFolder)
    {
        if (!string.IsNullOrEmpty(startupFolder))
        {
            TryDeleteFile(Path.Combine(startupFolder, StartupShortcutName));
        }

        // A per-user (pre-1.6) install keeps its own packages here and removes the whole folder itself; only a
        // machine-wide install uses this folder as Velopack's fallback cache (packages\ and a copy of Update.exe).
        if (string.IsNullOrEmpty(localAppFolder) || Directory.Exists(Path.Combine(localAppFolder, "current")))
        {
            return;
        }

        TryDeleteDirectory(Path.Combine(localAppFolder, "packages"));
        TryDeleteFile(Path.Combine(localAppFolder, "Update.exe"));
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(ex.Message); // Best effort: uninstall must not fail on a locked file.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(ex.Message);
        }
    }
}
