using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows.Interop;
using Microsoft.Win32;

namespace DotDeck.App.Infrastructure.Windows;

/// <summary>
/// Notification-area (tray) icon over <c>Shell_NotifyIcon</c>, so WPF needs no WinForms reference.
/// Left click raises <see cref="OpenRequested"/>; right click shows a native "열기 / 끝내기" menu.
/// The icon is the DotDeck symbol for the taskbar's light or dark theme at the small-icon size of the current DPI,
/// and is swapped when the theme, DPI or display changes.
/// </summary>
public sealed partial class TrayIcon : IDisposable
{
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2;
    private const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4;
    private const int WmApp = 0x8000;
    private const int CallbackMessage = WmApp + 1;
    private const int WmLButtonUp = 0x0202, WmRButtonUp = 0x0205;
    private const uint MfString = 0x0, MfSeparator = 0x800;
    private const uint TpmRightButton = 0x2, TpmReturnCmd = 0x100;
    private const int CmdOpen = 1, CmdExit = 2;
    private const int IdiApplication = 32512;
    private const int WmSettingChange = 0x001A, WmDisplayChange = 0x007E, WmDpiChanged = 0x02E0;
    private const int SmCxSmIcon = 49;
    private const uint IconResourceVersion = 0x00030000;
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static readonly int s_taskbarCreated = (int)RegisterWindowMessageW("TaskbarCreated");

    private readonly HwndSource _window;
    private IntPtr _icon;
    private bool _ownsIcon;
    private (bool LightTaskbar, int Size) _iconKey;
    private readonly string _tooltip;
    private bool _visible;
    private bool _disposed;

    public TrayIcon(string tooltip)
    {
        _tooltip = tooltip;
        // An invisible top-level window (not message-only) can become foreground, which the popup menu needs to close properly.
        _window = new HwndSource(new HwndSourceParameters("DotDeckTray") { Width = 0, Height = 0, WindowStyle = unchecked((int)0x80000000) });
        _window.AddHook(WndProc);
        UpdateIcon();
    }

    public event EventHandler? OpenRequested;

    public event EventHandler? ExitRequested;

    public bool IsVisible => _visible;

    public void Show()
    {
        if (_disposed || _visible)
        {
            return;
        }

        var data = CreateData(NifMessage | NifIcon | NifTip);
        _visible = ShellNotifyIconW(NimAdd, ref data);
    }

    public void Hide()
    {
        if (!_visible)
        {
            return;
        }

        var data = CreateData(0);
        _ = ShellNotifyIconW(NimDelete, ref data);
        _visible = false;
    }

    private unsafe NotifyIconData CreateData(uint flags)
    {
        var data = new NotifyIconData
        {
            cbSize = (uint)sizeof(NotifyIconData),
            hWnd = _window.Handle,
            uID = 1,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
        };
        var tip = _tooltip.AsSpan(0, Math.Min(_tooltip.Length, 127));
        tip.CopyTo(new Span<char>(data.szTip, 128));
        return data;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            switch ((int)lParam & 0xFFFF)
            {
                case WmLButtonUp:
                    OpenRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case WmRButtonUp:
                    ShowMenu();
                    break;
            }

            handled = true;
        }
        else if (msg == s_taskbarCreated && _visible)
        {
            // Explorer restarted: the old icon is gone, add it again (the DPI may have changed with it).
            _visible = false;
            UpdateIcon();
            Show();
        }
        else if (msg is WmSettingChange or WmDisplayChange or WmDpiChanged)
        {
            // Light/dark taskbar ("ImmersiveColorSet") or scale change: pick the matching symbol and size.
            UpdateIcon();
        }

        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            _ = AppendMenuW(menu, MfString, CmdOpen, "DotDeck 열기");
            _ = AppendMenuW(menu, MfSeparator, 0, null);
            _ = AppendMenuW(menu, MfString, CmdExit, "끝내기");
            _ = GetCursorPos(out var point);
            _ = SetForegroundWindow(_window.Handle);
            var command = TrackPopupMenuEx(menu, TpmRightButton | TpmReturnCmd, point.X, point.Y, _window.Handle, IntPtr.Zero);
            switch (command)
            {
                case CmdOpen:
                    OpenRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case CmdExit:
                    ExitRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        finally
        {
            _ = DestroyMenu(menu);
        }
    }

    /// <summary>Loads the icon for the current taskbar theme and DPI; replaces the shown one and frees the old handle.</summary>
    private void UpdateIcon()
    {
        if (_disposed)
        {
            return;
        }

        var key = (LightTaskbar: TaskbarUsesLightTheme(), Size: SmallIconSize());
        if (_icon != IntPtr.Zero && key == _iconKey)
        {
            return;
        }

        var (oldIcon, oldOwned) = (_icon, _ownsIcon);
        (_icon, _ownsIcon) = LoadTrayIcon(key.LightTaskbar, key.Size);
        _iconKey = key;
        if (_visible)
        {
            var data = CreateData(NifIcon);
            _ = ShellNotifyIconW(NimModify, ref data);
        }

        if (oldOwned && oldIcon != IntPtr.Zero && oldIcon != _icon)
        {
            _ = DestroyIcon(oldIcon);
        }
    }

    private int SmallIconSize()
    {
        var dpi = GetDpiForWindow(_window.Handle);
        var size = GetSystemMetricsForDpi(SmCxSmIcon, dpi == 0 ? 96 : dpi);
        return size > 0 ? size : 16;
    }

    /// <summary>Windows "default app mode" for the taskbar and tray: SystemUsesLightTheme=1 is a light taskbar.</summary>
    private static bool TaskbarUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static (IntPtr Icon, bool Owned) LoadTrayIcon(bool lightTaskbar, int size)
    {
        var name = lightTaskbar ? "Tray/tray-on-light.ico" : "Tray/tray-on-dark.ico";
        using (var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream(name))
        {
            if (stream is not null)
            {
                var ico = new byte[stream.Length];
                stream.ReadExactly(ico);
                var icon = CreateIconFromIco(ico, size);
                if (icon != IntPtr.Zero)
                {
                    return (icon, true);
                }
            }
        }

        // The exe's own icon when it has one; otherwise the system's generic application icon (shared, not destroyed).
        if (Environment.ProcessPath is { } exe && ExtractIconExW(exe, 0, IntPtr.Zero, out var small, 1) > 0 && small != IntPtr.Zero)
        {
            return (small, true);
        }

        return (LoadIconW(IntPtr.Zero, IdiApplication), false);
    }

    private static unsafe IntPtr CreateIconFromIco(byte[] ico, int size)
    {
        if (!TrySelectIcoFrame(ico, size, out var offset, out var length))
        {
            return IntPtr.Zero;
        }

        fixed (byte* frame = &ico[offset])
        {
            return CreateIconFromResourceEx(frame, (uint)length, 1, IconResourceVersion, size, size, 0);
        }
    }

    /// <summary>
    /// Picks the ICO frame for <paramref name="size"/>: the smallest frame at least that large, else the largest.
    /// Returns false for data that is not a well-formed ICO.
    /// </summary>
    internal static bool TrySelectIcoFrame(ReadOnlySpan<byte> ico, int size, out int offset, out int length)
    {
        offset = length = 0;
        if (ico.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(ico) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(ico[2..]) != 1)
        {
            return false;
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(ico[4..]);
        int best = -1, bestSize = 0;
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + (16 * i);
            if (entry + 16 > ico.Length)
            {
                return false;
            }

            var frameSize = ico[entry] == 0 ? 256 : ico[entry];
            var frameLength = BinaryPrimitives.ReadUInt32LittleEndian(ico[(entry + 8)..]);
            var frameOffset = BinaryPrimitives.ReadUInt32LittleEndian(ico[(entry + 12)..]);
            if (frameLength == 0 || frameOffset > (uint)ico.Length || frameLength > (uint)ico.Length - frameOffset)
            {
                continue;
            }

            var better = best < 0
                || (frameSize >= size && (bestSize < size || frameSize < bestSize))
                || (frameSize < size && bestSize < size && frameSize > bestSize);
            if (better)
            {
                (best, bestSize, offset, length) = (i, frameSize, (int)frameOffset, (int)frameLength);
            }
        }

        return best >= 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Hide();
        _disposed = true;
        _window.RemoveHook(WndProc);
        _window.Dispose();
        if (_ownsIcon && _icon != IntPtr.Zero)
        {
            _ = DestroyIcon(_icon);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        public fixed char szTip[128];
        public uint dwState;
        public uint dwStateMask;
        public fixed char szInfo[256];
        public uint uVersion;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShellNotifyIconW(uint message, ref NotifyIconData data);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint ExtractIconExW(string file, int index, IntPtr large, out IntPtr small, uint count);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessageW(string name);

    [LibraryImport("user32.dll")]
    private static partial IntPtr LoadIconW(IntPtr instance, IntPtr name);

    [LibraryImport("user32.dll")]
    private static unsafe partial IntPtr CreateIconFromResourceEx(byte* bits, uint size, int isIcon, uint version, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int index, uint dpi);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr icon);

    [LibraryImport("user32.dll")]
    private static partial IntPtr CreatePopupMenu();

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenuW(IntPtr menu, uint flags, nuint id, string? text);

    [LibraryImport("user32.dll")]
    private static partial int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr parameters);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(IntPtr menu);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hwnd);
}
