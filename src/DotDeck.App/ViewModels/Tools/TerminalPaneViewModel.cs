using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DotDeck.Core.AiCli.Sessions;

namespace DotDeck.App.ViewModels.Tools;

/// <summary>
/// One terminal tab in the bottom area (ContentId <c>tool.terminal:&lt;id&gt;</c>, never restored from a saved
/// layout). The view (TerminalView) connects xterm.js to <see cref="Connection"/>. Closing the tab asks the shell,
/// which stops a running process tree after confirmation and removes the tab.
/// </summary>
public sealed partial class TerminalPaneViewModel : ToolPaneViewModel
{
    public const string ContentIdPrefix = "tool.terminal:";

    private readonly string _baseTitle;
    private readonly Action<string> _copy;

    public TerminalPaneViewModel(string id, string title, TerminalConnection connection, Action<string> copy, SynchronizationContext? ui)
        : base(ContentIdPrefix + id, title, ToolLocation.Bottom)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(copy);
        _baseTitle = title;
        _copy = copy;
        Connection = connection;
        _ = WatchExitAsync(ui);
    }

    public TerminalConnection Connection { get; }

    [ObservableProperty]
    private bool _isRunning = true;

    public int? ExitCode { get; private set; }

    /// <summary>The user closed (hid) the tab; the shell decides whether it really goes away.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The process ended; raised on the UI thread.</summary>
    public event EventHandler? Ended;

    /// <summary>Line written into the terminal once the process has ended.</summary>
    public string EndNotice => ExitCode is { } code
        ? string.Create(CultureInfo.InvariantCulture, $"\r\n\u001b[90m[프로세스가 종료되었습니다. 코드 {code}]\u001b[0m\r\n")
        : "\r\n\u001b[90m[프로세스가 종료되었습니다]\u001b[0m\r\n";

    /// <summary>Text selected in the terminal and copied by the user (Ctrl+C with a selection).</summary>
    public void Copy(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            _copy(text);
        }
    }

    protected override void OnVisibilityChanged(bool isVisible)
    {
        if (!isVisible)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task WatchExitAsync(SynchronizationContext? ui)
    {
        int code;
        try
        {
            code = await Connection.Exited.ConfigureAwait(false);
            await Connection.OutputEnded.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            code = Connection.Exited.Result;
        }

        void Apply()
        {
            ExitCode = code;
            IsRunning = false;
            Title = _baseTitle + " (종료됨)";
            Ended?.Invoke(this, EventArgs.Empty);
        }

        if (ui is null)
        {
            Apply();
        }
        else
        {
            ui.Post(_ => Apply(), null);
        }
    }
}
