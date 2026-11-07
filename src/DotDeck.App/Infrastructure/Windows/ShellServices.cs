using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using DotDeck.App.Services;

namespace DotDeck.App.Infrastructure.Windows;

public sealed class ClipboardService : IClipboardService
{
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
}
