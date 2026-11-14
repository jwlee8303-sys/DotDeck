using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace DotDeck.App.Infrastructure.Windows;

/// <summary>
/// Notification-area (tray) icon over <c>Shell_NotifyIcon</c>, so WPF needs no WinForms reference.
/// Left click raises <see cref="OpenRequested"/>; right click shows a native "열기 / 끝내기" menu.
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

    private static readonly int s_taskbarCreated = (int)RegisterWindowMessageW("TaskbarCreated");

    private readonly HwndSource _window;
    private readonly IntPtr _icon;
    private readonly bool _ownsIcon;
    private readonly string _tooltip;
    private bool _visible;
    private bool _disposed;

    public TrayIcon(string tooltip)
    {
        _tooltip = tooltip;
        // An invisible top-level window (not message-only) can become foreground, which the popup menu needs to close properly.
        _window = new HwndSource(new HwndSourceParameters("DotDeckTray") { Width = 0, Height = 0, WindowStyle = unchecked((int)0x80000000) });
        _window.AddHook(WndProc);
        (_icon, _ownsIcon) = LoadAppIcon();
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
            // Explorer restarted: the old icon is gone, add it again.
            _visible = false;
            Show();
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

    private static (IntPtr Icon, bool Owned) LoadAppIcon()
    {
        // The exe's own icon when it has one; otherwise the system's generic application icon (shared, not destroyed).
        if (Environment.ProcessPath is { } exe && ExtractIconExW(exe, 0, IntPtr.Zero, out var small, 1) > 0 && small != IntPtr.Zero)
        {
            return (small, true);
        }

        return (LoadIconW(IntPtr.Zero, IdiApplication), false);
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
