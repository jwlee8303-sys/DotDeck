using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DotDeck.App.Infrastructure.Windows;

/// <summary>Asks DWM to draw the native title bar in dark mode (Windows 10 20H1+ / Windows 11).</summary>
public static partial class DarkTitleBar
{
    private const int DwmwaUseImmersiveDarkMode = 20;

    /// <summary>Applies now if the window has a handle, otherwise once it is created.</summary>
    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            Apply(window);
        }
        else
        {
            window.SourceInitialized += (_, _) => Apply(window);
        }
    }

    private static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var enabled = DotDeck.App.Themes.ThemeManager.Current == DotDeck.App.Themes.ThemeKind.Dark ? 1 : 0;
        // Failure (older Windows) is harmless: the title bar simply stays light.
        _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
