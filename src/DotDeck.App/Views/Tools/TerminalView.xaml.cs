using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DotDeck.App.Infrastructure;
using DotDeck.App.Infrastructure.Windows;
using DotDeck.App.Themes;
using DotDeck.App.ViewModels.Tools;
using DotDeck.Core.AiCli.Sessions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DotDeck.App.Views.Tools;

/// <summary>
/// xterm.js in WebView2, connected to a <see cref="TerminalPaneViewModel"/>'s <see cref="TerminalConnection"/>.
/// The page and its scripts come only from DotDeck's embedded files on <c>https://term.dotdeck.local</c>
/// (<see cref="TerminalAssets"/>); every other request, navigation, new window, download and permission is refused.
/// Output is batched per dispatcher turn; input, resizes and copies arrive as web messages from the page.
/// The WebView exists while the view is loaded; a re-created view replays the connection's recent output.
/// </summary>
public partial class TerminalView : UserControl
{
    private const string PageUrl = "https://" + TerminalAssets.Host + "/terminal.html";
    private const string ResponseHeaders =
        "Content-Type: {0}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n"
        + "Content-Security-Policy: default-src 'none'; script-src https://term.dotdeck.local; style-src https://term.dotdeck.local 'unsafe-inline'; img-src data:; base-uri 'none'; form-action 'none'";

    private readonly Lock _pendingGate = new();
    private readonly StringBuilder _pending = new();
    private bool _flushScheduled;
    private WebView2? _web;
    private bool _initializing;
    private TerminalPaneViewModel? _pane;
    private Action<string>? _viewer;

    public TerminalView()
    {
        InitializeComponent();
        Loaded += (_, _) => _ = StartAsync();
        Unloaded += (_, _) => Stop();
        DataContextChanged += (_, _) =>
        {
            Stop();
            _ = StartAsync();
        };
    }

    private async Task StartAsync()
    {
        if (_web is not null || _initializing || !IsLoaded || DataContext is not TerminalPaneViewModel pane)
        {
            return;
        }

        if (WebViewEnvironment.RuntimeVersion is null)
        {
            ShowMessage("Microsoft Edge WebView2 런타임이 없어 터미널을 표시할 수 없습니다.");
            return;
        }

        _initializing = true;
        var web = new WebView2 { DefaultBackgroundColor = ToDrawingColor("DD.Color.Terminal.Background") };
        try
        {
            Host.Children.Add(web);
            await web.EnsureCoreWebView2Async(await WebViewEnvironment.GetAsync());
            if (!IsLoaded || DataContext != pane)
            {
                Host.Children.Remove(web);
                web.Dispose();
                return;
            }

            _web = web;
            _pane = pane;
            Configure(web.CoreWebView2);
            pane.PropertyChanged += OnPanePropertyChanged;
            pane.Ended += OnPaneEnded;
            ThemeManager.Changed += OnThemeChanged;
            Message.Visibility = Visibility.Collapsed;
            web.CoreWebView2.Navigate(PageUrl);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or IOException or UnauthorizedAccessException or WebView2RuntimeNotFoundException)
        {
            Host.Children.Remove(web);
            web.Dispose();
            ShowMessage($"터미널을 표시하지 못했습니다: {ex.Message}");
        }
        finally
        {
            _initializing = false;
        }
    }

    private void Configure(CoreWebView2 core)
    {
        var settings = core.Settings;
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.IsWebMessageEnabled = true;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsScriptEnabled = true;

        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;
        core.NavigationStarting += (_, e) =>
        {
            if (!IsOwnPage(e.Uri))
            {
                e.Cancel = true;
            }
        };
        core.FrameNavigationStarting += (_, e) => e.Cancel = true;
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.WebMessageReceived += OnWebMessageReceived;
    }

    private static bool IsOwnPage(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, TerminalAssets.Host, StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort
        && string.Equals(uri.AbsolutePath, "/terminal.html", StringComparison.Ordinal);

    // Served from memory: only GET requests for the embedded page files; anything else is 403.
    private static void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (sender is not CoreWebView2 core)
        {
            return;
        }

        var served = string.Equals(e.Request.Method, "GET", StringComparison.OrdinalIgnoreCase)
                     && Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)
            ? TerminalAssets.Open(uri)
            : null;
        e.Response = served is { } file
            ? core.Environment.CreateWebResourceResponse(file.Content, 200, "OK", string.Format(System.Globalization.CultureInfo.InvariantCulture, ResponseHeaders, file.ContentType))
            : core.Environment.CreateWebResourceResponse(null, 403, "Forbidden", string.Empty);
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_pane is not { } pane || !IsOwnPage(e.Source))
        {
            return;
        }

        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            var root = message.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("t", out var type))
            {
                return;
            }

            switch (type.GetString())
            {
                case "ready":
                    Connect(pane, Size(root));
                    break;
                case "input" when pane.IsRunning:
                    pane.Connection.Send(Text(root));
                    break;
                case "binary" when pane.IsRunning:
                    pane.Connection.SendBinary(Text(root));
                    break;
                case "resize":
                    if (Size(root) is { } size)
                    {
                        pane.Connection.Resize(size);
                    }

                    break;
                case "copy":
                    pane.Copy(Text(root));
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // A malformed message from the page is ignored.
        }
    }

    private static string Text(JsonElement root) =>
        root.TryGetProperty("d", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? string.Empty : string.Empty;

    private static TerminalSize? Size(JsonElement root) =>
        root.TryGetProperty("c", out var c) && root.TryGetProperty("r", out var r)
        && c.TryGetInt32(out var columns) && r.TryGetInt32(out var rows) && columns > 0 && rows > 0
            ? new TerminalSize(columns, rows)
            : null;

    /// <summary>The page is ready: send the appearance and the recent output, then stream what follows.</summary>
    private void Connect(TerminalPaneViewModel pane, TerminalSize? size)
    {
        if (_viewer is not null)
        {
            pane.Connection.Detach(_viewer);
        }

        lock (_pendingGate)
        {
            _pending.Clear();
        }

        _viewer = OnOutput;
        var replay = pane.Connection.Attach(_viewer);
        if (!pane.IsRunning)
        {
            replay += pane.EndNotice;
        }

        Post(new { t = "init", theme = Theme(), fontFamily = CssFontFamily(), fontSize = CssFontSize(), replay });
        if (!pane.IsRunning)
        {
            Post(new { t = "stdin", enabled = false });
        }

        if (size is { } s)
        {
            pane.Connection.Resize(s);
        }
    }

    // Reader thread: collect, and flush once per dispatcher turn so heavy output becomes few large messages.
    private void OnOutput(string text)
    {
        lock (_pendingGate)
        {
            _pending.Append(text);
            if (_flushScheduled)
            {
                return;
            }

            _flushScheduled = true;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Background, Flush);
    }

    private void Flush()
    {
        string text;
        lock (_pendingGate)
        {
            _flushScheduled = false;
            text = _pending.ToString();
            _pending.Clear();
        }

        if (text.Length > 0)
        {
            Post(new { t = "output", d = text });
        }
    }

    private void OnPaneEnded(object? sender, EventArgs e)
    {
        if (_pane is { } pane)
        {
            Flush();
            Post(new { t = "notice", d = pane.EndNotice });
            Post(new { t = "stdin", enabled = false });
        }
    }

    private void OnPanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalPaneViewModel.IsActive) && _pane?.IsActive == true && _web is { } web)
        {
            web.Focus();
            Post(new { t = "focus" });
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e) =>
        Post(new { t = "theme", theme = Theme(), fontFamily = CssFontFamily(), fontSize = CssFontSize() });

    private void Post(object message)
    {
        if (_web?.CoreWebView2 is not { } core)
        {
            return;
        }

        try
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(message));
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // The WebView is closing.
        }
    }

    private void Stop()
    {
        if (_pane is { } pane)
        {
            if (_viewer is not null)
            {
                pane.Connection.Detach(_viewer);
            }

            pane.PropertyChanged -= OnPanePropertyChanged;
            pane.Ended -= OnPaneEnded;
        }

        ThemeManager.Changed -= OnThemeChanged;
        _viewer = null;
        _pane = null;
        lock (_pendingGate)
        {
            _pending.Clear();
        }

        if (_web is not null)
        {
            Host.Children.Remove(_web);
            _web.Dispose();
            _web = null;
        }
    }

    private void ShowMessage(string text)
    {
        Message.Text = text;
        Message.Visibility = Visibility.Visible;
    }

    private Dictionary<string, string> Theme()
    {
        string Hex(string name) => TryFindResource("DD.Color.Terminal." + name) is Color c ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : string.Empty;

        return new Dictionary<string, string>
        {
            ["background"] = Hex("Background"),
            ["foreground"] = Hex("Foreground"),
            ["cursor"] = Hex("Cursor"),
            ["cursorAccent"] = Hex("Background"),
            ["selectionBackground"] = Hex("Selection"),
            ["black"] = Hex("Black"),
            ["red"] = Hex("Red"),
            ["green"] = Hex("Green"),
            ["yellow"] = Hex("Yellow"),
            ["blue"] = Hex("Blue"),
            ["magenta"] = Hex("Magenta"),
            ["cyan"] = Hex("Cyan"),
            ["white"] = Hex("White"),
            ["brightBlack"] = Hex("BrightBlack"),
            ["brightRed"] = Hex("BrightRed"),
            ["brightGreen"] = Hex("BrightGreen"),
            ["brightYellow"] = Hex("BrightYellow"),
            ["brightBlue"] = Hex("BrightBlue"),
            ["brightMagenta"] = Hex("BrightMagenta"),
            ["brightCyan"] = Hex("BrightCyan"),
            ["brightWhite"] = Hex("BrightWhite"),
        };
    }

    /// <summary>The editor font as a CSS font list (each family quoted), ending with monospace.</summary>
    private string CssFontFamily()
    {
        var source = TryFindResource("DD.Font.Editor") is FontFamily f ? f.Source : "Consolas";
        var families = source.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(n => n.All(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_'))
            .Select(n => "'" + n + "'");
        return string.Join(", ", families.Append("monospace"));
    }

    private double CssFontSize() => TryFindResource("DD.FontSize.Editor") is double size && size > 0 ? size : 13;

    private System.Drawing.Color ToDrawingColor(string key) =>
        TryFindResource(key) is Color c ? System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B) : System.Drawing.Color.Black; // theme-color-ok: converts a theme resource color for WebView2
}
