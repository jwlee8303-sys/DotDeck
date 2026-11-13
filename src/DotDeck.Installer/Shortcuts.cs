using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace DotDeck.Installer;

/// <summary>Current-user shortcuts: the "run at sign-in" entry and leftovers of a per-user (1.5) install.</summary>
internal static class Shortcuts
{
    public const string FileName = "DotDeck.lnk";

    /// <summary>
    /// The current user's Startup folder. Created when missing: a new profile may not have it yet, and then the plain
    /// lookup returns an empty string.
    /// </summary>
    public static string StartupFolder => UserFolder(Environment.SpecialFolder.Startup);

    public static string StartupShortcut => Path.Combine(StartupFolder, FileName);

    public static bool AutoStartEnabled => File.Exists(StartupShortcut);

    /// <summary>Points at the install folder's DotDeck.exe launcher, which stays valid across updates.</summary>
    public static void SetAutoStart(bool enabled, string installFolder)
    {
        var link = StartupShortcut;
        if (!enabled)
        {
            if (File.Exists(link)) File.Delete(link);
            return;
        }

        Create(link, Path.Combine(installFolder, "DotDeck.exe"), installFolder, "DotDeck (Windows 로그인 시 자동 실행)");
    }

    /// <summary>Deletes .lnk files of the current user that point into <paramref name="root"/> (desktop, Start menu, Startup).</summary>
    public static void RemoveUserShortcutsInto(string root)
    {
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs, Environment.SpecialFolderOption.DoNotVerify);
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolderOption.DoNotVerify),
            programs,
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu, Environment.SpecialFolderOption.DoNotVerify),
            Environment.GetFolderPath(Environment.SpecialFolder.Startup, Environment.SpecialFolderOption.DoNotVerify),
            programs.Length == 0 ? "" : Path.Combine(programs, "DotDeck"),
        };
        foreach (var folder in folders.Where(f => f.Length > 0 && Directory.Exists(f)))
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

    /// <summary>Target of a shortcut, or null when it cannot be read.</summary>
    public static string? TryGetTarget(string link)
    {
        object? instance = null;
        try
        {
            instance = new ShellLink();
            ((IPersistFile)instance).Load(link, 0);
            var buffer = new StringBuilder(1024);
            ((IShellLinkW)instance).GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0x4 /* SLGP_RAWPATH */);
            return buffer.Length == 0 ? null : buffer.ToString();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            Debug.WriteLine(ex.Message);
            return null;
        }
        finally
        {
            if (instance is not null) Marshal.FinalReleaseComObject(instance);
        }
    }

    private static string UserFolder(Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.Create);
        if (path.Length == 0) throw new IOException("Windows 사용자 폴더(" + folder + ")를 찾지 못했습니다.");
        return path;
    }

    /// <summary>Writes the shortcut with the Unicode shell API, so Korean and other non-ANSI paths stay intact.</summary>
    private static void Create(string link, string target, string workingDirectory, string description)
    {
        object instance = new ShellLink();
        try
        {
            var shellLink = (IShellLinkW)instance;
            shellLink.SetPath(target);
            shellLink.SetWorkingDirectory(workingDirectory);
            shellLink.SetDescription(description);
            shellLink.SetIconLocation(target, 0);
            ((IPersistFile)instance).Save(link, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(instance);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine(ex.Message); }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxPath, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
