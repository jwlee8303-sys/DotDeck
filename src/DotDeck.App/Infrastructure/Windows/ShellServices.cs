using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using DotDeck.App.Services;

namespace DotDeck.App.Infrastructure.Windows;

public sealed class ClipboardService : IClipboardService
{
    // Explorer's "Preferred DropEffect" values.
    private const int DropEffectCopy = 1;
    private const int DropEffectMove = 2;
    private const int DropEffectLink = 4;
    private const string PreferredDropEffect = "Preferred DropEffect";

    public bool TrySetText(string text)
    {
        try
        {
            Clipboard.SetDataObject(text, copy: true);
            return true;
        }
        catch (COMException)
        {
            // CLIPBRD_E_CANT_OPEN: another application holds the clipboard.
            return false;
        }
    }

    public bool TrySetFiles(IReadOnlyList<string> paths, bool cut)
    {
        var list = new StringCollection();
        foreach (var path in paths)
        {
            list.Add(path);
        }

        var data = new DataObject();
        data.SetFileDropList(list);
        var effect = cut ? DropEffectMove : DropEffectCopy | DropEffectLink;
        data.SetData(PreferredDropEffect, new MemoryStream(BitConverter.GetBytes(effect)));
        try
        {
            Clipboard.SetDataObject(data, copy: true);
            return true;
        }
        catch (COMException)
        {
            return false;
        }
    }

    public ClipboardFiles? TryGetFiles()
    {
        try
        {
            if (!Clipboard.ContainsFileDropList())
            {
                return null;
            }

            var paths = Clipboard.GetFileDropList().Cast<string>().Where(p => !string.IsNullOrEmpty(p)).ToList();
            var cut = Clipboard.GetData(PreferredDropEffect) is MemoryStream stream
                && stream.Length >= 4
                && ReadInt(stream) is var effect
                && (effect & DropEffectMove) != 0
                && (effect & DropEffectCopy) == 0;
            return paths.Count == 0 ? null : new ClipboardFiles(paths, cut);
        }
        catch (COMException)
        {
            return null;
        }
    }

    public void ClearCutFiles(IReadOnlyList<string> paths)
    {
        if (TryGetFiles() is { IsCut: true } current
            && current.Paths.Count == paths.Count
            && current.Paths.All(p => paths.Contains(p, StringComparer.OrdinalIgnoreCase)))
        {
            try
            {
                Clipboard.Clear();
            }
            catch (COMException)
            {
            }
        }
    }

    private static int ReadInt(MemoryStream stream)
    {
        var bytes = new byte[4];
        stream.Position = 0;
        stream.ReadExactly(bytes);
        return BitConverter.ToInt32(bytes);
    }
}

public sealed class ShellLauncher : IShellLauncher
{
    public void RevealInExplorer(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        // Windows paths cannot contain '"', so quoting is safe. explorer.exe expects /select,"<path>" exactly.
        var start = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = false,
            Arguments = Directory.Exists(path) ? $"\"{path}\"" : $"/select,\"{path}\"",
        };

        using var _ = Process.Start(start);
    }

    public bool OpenInVsCode(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (FindVsCode() is not { } code)
        {
            return false;
        }

        // Code.exe directly (not code.cmd through cmd.exe), so the path is never parsed by a shell.
        var start = new ProcessStartInfo { FileName = code, UseShellExecute = false };
        start.ArgumentList.Add(path);
        using var _ = Process.Start(start);
        return true;
    }

    public void OpenTerminal(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        try
        {
            // wt treats ';' as a command separator; escape it so the path stays one argument.
            var start = new ProcessStartInfo { FileName = "wt.exe", UseShellExecute = false };
            start.ArgumentList.Add("-d");
            start.ArgumentList.Add(directory.Replace(";", "\\;", StringComparison.Ordinal));
            using var _ = Process.Start(start);
        }
        catch (Win32Exception)
        {
            // Windows Terminal is not installed: a PowerShell console in that folder.
            var start = new ProcessStartInfo { FileName = "powershell.exe", UseShellExecute = true, WorkingDirectory = directory };
            using var _ = Process.Start(start);
        }
    }

    public bool OpenElevatedTerminal(string shellExecutable, string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(shellExecutable);
        ArgumentException.ThrowIfNullOrEmpty(directory);
        // An elevated process ignores WorkingDirectory (it starts in System32), so the shell changes folder itself.
        // The command is passed encoded so the path is never parsed by the command line.
        var start = new ProcessStartInfo
        {
            FileName = shellExecutable,
            UseShellExecute = true,
            Verb = "runas",
            Arguments = "-NoExit -EncodedCommand " + ElevatedStartCommand(directory),
        };
        try
        {
            using var _ = Process.Start(start);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
    }

    private const int ErrorCancelled = 1223;

    /// <summary>Base64 (UTF-16LE) of "Set-Location -LiteralPath '<directory>'" for PowerShell's -EncodedCommand.</summary>
    internal static string ElevatedStartCommand(string directory) =>
        Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(
            "Set-Location -LiteralPath '" + directory.Replace("'", "''", StringComparison.Ordinal) + "'"));

    /// <summary>Finds Code.exe: per-user and machine installs, then a "...\Microsoft VS Code\bin" PATH entry.</summary>
    private static string? FindVsCode()
    {
        var candidates = new List<string>();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (localAppData.Length > 0)
        {
            candidates.Add(Path.Combine(localAppData, "Programs", "Microsoft VS Code", "Code.exe"));
        }

        if (programFiles.Length > 0)
        {
            candidates.Add(Path.Combine(programFiles, "Microsoft VS Code", "Code.exe"));
        }

        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            var trimmed = entry.Trim().Trim('"');
            if (trimmed.Length > 0 && Path.IsPathFullyQualified(trimmed) && File.Exists(Path.Combine(trimmed, "code.cmd"))
                && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(trimmed)) is { } installDir)
            {
                candidates.Add(Path.Combine(installDir, "Code.exe"));
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }
}
