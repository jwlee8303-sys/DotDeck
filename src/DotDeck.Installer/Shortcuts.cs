using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DotDeck.Installer;

/// <summary>Current-user shortcuts: the "run at sign-in" entry and leftovers of a per-user (1.5) install.</summary>
internal static class Shortcuts
{
    public const string FileName = "DotDeck.lnk";

    public static string StartupShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), FileName);

    public static bool AutoStartEnabled => File.Exists(StartupShortcut);

    /// <summary>Points at the install folder's DotDeck.exe launcher, which stays valid across updates.</summary>
    public static void SetAutoStart(bool enabled, string installFolder)
    {
        if (!enabled)
        {
            if (File.Exists(StartupShortcut)) File.Delete(StartupShortcut);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(StartupShortcut)!);
        Create(StartupShortcut, Path.Combine(installFolder, "DotDeck.exe"), installFolder, "DotDeck (Windows 로그인 시 자동 실행)");
    }

    /// <summary>Deletes .lnk files of the current user that point into <paramref name="root"/> (desktop, Start menu, Startup).</summary>
    public static void RemoveUserShortcutsInto(string root)
    {
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "DotDeck"),
        };
        foreach (var folder in folders.Where(Directory.Exists))
        {
            foreach (var link in Directory.GetFiles(folder, "*.lnk"))
            {
                var target = TryGetTarget(link);
                if (target is not null && Paths.IsLocalFullPath(target) && Paths.IsInside(target, root))
                {
                    TryDelete(link);
                }
            }
        }
    }

    private static void Create(string link, string target, string workingDirectory, string description)
    {
        Invoke(link, shortcut =>
        {
            Set(shortcut, "TargetPath", target);
            Set(shortcut, "WorkingDirectory", workingDirectory);
            Set(shortcut, "Description", description);
            Set(shortcut, "IconLocation", target + ",0");
            shortcut.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            return null;
        });
    }

    private static string? TryGetTarget(string link)
    {
        try
        {
            return Invoke(link, shortcut => shortcut.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null) as string);
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException or IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine(ex.Message);
            return null;
        }
    }

    private static string? Invoke(string link, Func<object, string?> action)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new COMException("WScript.Shell을 사용할 수 없습니다.");
        var shell = Activator.CreateInstance(shellType)!;
        object? shortcut = null;
        try
        {
            shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [link])!;
            return action(shortcut);
        }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void Set(object target, string name, string value) =>
        target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, [value]);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine(ex.Message); }
    }
}
