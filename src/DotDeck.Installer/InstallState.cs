using System.Diagnostics;
using Microsoft.Win32;

namespace DotDeck.Installer;

/// <summary>Existing DotDeck installs on this machine, read from the Windows uninstall entries.</summary>
internal sealed class InstallState
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\";

    /// <summary>Machine-wide install (the Velopack MSI writes HKLM\...\Uninstall\MSI:{id}).</summary>
    public string? MachineFolder { get; private set; }
    public string? MachineVersion { get; private set; }

    /// <summary>Per-user install from DotDeck 1.5 (Velopack Setup.exe, HKCU\...\Uninstall\{id}), anywhere on disk.</summary>
    public string? LegacyRoot { get; private set; }

    public static InstallState Read(string packId)
    {
        var state = new InstallState();
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var machine = hklm.OpenSubKey(UninstallKey + "MSI:" + packId);
            if (machine?.GetValue("InstallLocation") is string location && Paths.IsLocalFullPath(location.TrimEnd('\\')))
            {
                state.MachineFolder = location.TrimEnd('\\');
                state.MachineVersion = machine.GetValue("DisplayVersion") as string;
            }

            using var user = Registry.CurrentUser.OpenSubKey(UninstallKey + packId);
            if (user?.GetValue("InstallLocation") is string root && IsLegacyRoot(root.TrimEnd('\\')))
            {
                state.LegacyRoot = root.TrimEnd('\\');
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Debug.WriteLine(ex.Message); // Unreadable entries: treat as not installed.
        }

        return state;
    }

    /// <summary>A Velopack per-user root: Update.exe with current\ or packages\, and not the machine-wide MSI layout.</summary>
    public static bool IsLegacyRoot(string root) =>
        Paths.IsLocalFullPath(root)
        && File.Exists(Path.Combine(root, "Update.exe"))
        && (Directory.Exists(Path.Combine(root, "current")) || Directory.Exists(Path.Combine(root, "packages")))
        && !File.Exists(Path.Combine(root, "current", ".msi-installed"));

    /// <summary>Removes the old per-user uninstall entry (only if it still points at <paramref name="root"/>).</summary>
    public static void RemoveLegacyEntry(string packId, string root)
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey + packId);
        if (key?.GetValue("InstallLocation") is string location && Paths.SameFolder(location, root))
        {
            key.Close();
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey + packId, throwOnMissingSubKey: false);
        }
    }

    /// <summary>True when an installed version is newer than <paramref name="version"/> (setup never downgrades).</summary>
    public static bool IsNewer(string? installed, string version) =>
        TryParse(installed, out var a) && TryParse(version, out var b) && a > b;

    private static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var core = text!.Split('-', '+')[0];
        return Version.TryParse(core, out version!);
    }
}
