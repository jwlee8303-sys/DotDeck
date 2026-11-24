using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using DotDeck.App.Infrastructure;
using DotDeck.App.Infrastructure.Windows;
using DotDeck.Core.AiCli.Sessions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DotDeck.App.Views.Tools;

/// <summary>
/// Reads the screen of a terminal tab that is not shown (its view, and so its xterm.js, does not exist): the tab's recent
/// output is rendered by the same terminal page in one off-screen window and the screen lines are read back. The result
/// is an estimate (the replay buffer may start in the middle of a frame), so DotDeck never types into a terminal based
/// on it. UI thread only; one render at a time.
/// </summary>
public sealed class TerminalScreenProbe : IDisposable
{
    private const string PageUrl = "https://" + TerminalAssets.Host + "/terminal.html";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Window? _window;
    private WebView2? _web;
    private TaskCompletionSource<bool>? _ready;
    private TaskCompletionSource<string?>? _screen;
    private int _id;
    private bool _disposed;

    /// <summary>The screen lines after rendering <paramref name="replay"/> at <paramref name="size"/>, or null.</summary>
    public async Task<string?> RenderAsync(string replay, TerminalSize size, CancellationToken cancellationToken)
    {
        if (_disposed || WebViewEnvironment.RuntimeVersion is null)
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (!await EnsurePageAsync().ConfigureAwait(true) || _web?.CoreWebView2 is not { } core)
            {
                return null;
            }

            var id = ++_id;
            _screen = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            core.PostWebMessageAsJson(JsonSerializer.Serialize(new { t = "probe", id, c = size.Columns, r = size.Rows, replay }));
            return await _screen.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or COMException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            _screen = null;
            _gate.Release();
        }
    }

    private async Task<bool> EnsurePageAsync()
    {
        if (_ready is { } ready)
        {
            return await ready.Task.ConfigureAwait(true);
        }

        _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var web = new WebView2();
            // Off screen, never activated, not in the taskbar or Alt+Tab: it only hosts the page.
            _window = new Window
            {
                Width = 1200,
                Height = 900,
                Left = -32000,
                Top = -32000,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Focusable = false,
                Title = "DotDeck terminal probe",
                Content = web,
            };
            _window.Show();
            await web.EnsureCoreWebView2Async(await WebViewEnvironment.GetAsync().ConfigureAwait(true)).ConfigureAwait(true);
            _web = web;
            TerminalView.ConfigurePage(web.CoreWebView2, OnMessage);
            web.CoreWebView2.Navigate(PageUrl);
            return await _ready.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or IOException or UnauthorizedAccessException or WebView2RuntimeNotFoundException or TimeoutException)
        {
            _ready.TrySetResult(false);
            return false;
        }
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!TerminalView.IsOwnPage(e.Source))
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
                    _ready?.TrySetResult(true);
                    break;
                case "screen" when root.TryGetProperty("id", out var id) && id.TryGetInt32(out var value) && value == _id:
                    _screen?.TrySetResult(root.TryGetProperty("d", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null);
                    break;
            }
        }
        catch (JsonException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _web?.Dispose();
        _window?.Close();
        _gate.Dispose();
    }
}
