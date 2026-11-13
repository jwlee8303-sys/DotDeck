using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;

namespace DotDeck.Installer;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        // A single EXE has no .config to opt in: turn on the 4.7.1–4.8 UI Automation fixes (roles, names) here,
        // before any WinForms type reads the switches.
        AppContext.SetSwitch("Switch.UseLegacyAccessibilityFeatures", false);
        AppContext.SetSwitch("Switch.UseLegacyAccessibilityFeatures.2", false);
        AppContext.SetSwitch("Switch.UseLegacyAccessibilityFeatures.3", false);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var packId = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "PackId")?.Value ?? "DotDeck";
        using var form = new InstallForm(packId, DefaultFolder(packId));
        Application.Run(form);
        return form.Installed ? 0 : 1;
    }

    /// <summary>An existing install (Velopack registers its uninstall entry per user) keeps its folder; else %LocalAppData%\{id}.</summary>
    private static string DefaultFolder(string packId)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + packId);
            if (key?.GetValue("InstallLocation") is string existing && Path.IsPathRooted(existing) && Directory.Exists(existing))
            {
                return existing.TrimEnd('\\');
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Debug.WriteLine(ex.Message); // Unreadable entry: fall back to the default folder.
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), packId);
    }
}
