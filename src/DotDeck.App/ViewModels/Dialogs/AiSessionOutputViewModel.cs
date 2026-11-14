using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DotDeck.Core.AiCli.Launch;
using DotDeck.Core.AiCli.Sessions;

namespace DotDeck.App.ViewModels.Dialogs;

/// <summary>
/// "AI 출력" window (opened from the AI 모니터): the parsed output, state, prompt and command line of a non-interactive
/// session. Output and state events arrive on background threads and go through <c>post</c> to the UI thread.
/// </summary>
public sealed partial class AiSessionOutputViewModel : ObservableObject
{
    public const int MaxLines = 5000;

    private readonly Action<Action> _post;
    private bool _detached;

    public AiSessionOutputViewModel(AiSession session, Action<Action> post)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        _post = post ?? throw new ArgumentNullException(nameof(post));

        // Lines raised before this window existed come with the subscription, so none is lost or shown twice.
        foreach (var (line, isError) in session.AttachOutput(OnOutput))
        {
            AddLine(line, isError);
        }

        session.StateChanged += OnStateChanged;
    }

    public AiSession Session { get; }

    public ObservableCollection<AiOutputEvent> Lines { get; } = [];

    public string Title => Session.Title;

    public string WindowTitle => "AI 출력 - " + Session.Title;

    public string StartedText => Session.StartedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public string CommandLine => Session.CommandLinePreview;

    /// <summary>The prompt as shown (secret-looking assignments masked like all AI output).</summary>
    public string? Prompt => Session.Request.Prompt is { } prompt ? DotDeck.Core.Security.SecretMasker.MaskText(prompt) : null;

    public bool IsRunning => Session.State == AiSessionState.Running;

    public string StateText => Session.State switch
    {
        AiSessionState.Running when Session.Cli == CliKind.Codex => "실행 중 · Codex sandbox",
        AiSessionState.Running when !Session.GuardConnected => "실행 중 · 가드 확인 대기",
        AiSessionState.Running => "실행 중 · 가드 적용됨",
        AiSessionState.Exited => "완료",
        AiSessionState.Stopped => "중지됨",
        _ => Session.ExitCode is { } code ? $"실패 (종료 코드 {code})" : "실패",
    };

    public string? Message => Session.Message;

    public bool IsFailed => Session.State == AiSessionState.Failed;

    /// <summary>Stops following the session (the window closed).</summary>
    public void Detach()
    {
        if (_detached)
        {
            return;
        }

        _detached = true;
        Session.DetachOutput(OnOutput);
        Session.StateChanged -= OnStateChanged;
    }

    private void OnOutput(AiSession session, string line, bool isError) => _post(() =>
    {
        if (!_detached)
        {
            AddLine(line, isError);
        }
    });

    private void OnStateChanged(AiSession session) => _post(() =>
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(Message));
    });

    private void AddLine(string line, bool isError)
    {
        var events = Session.Cli == CliKind.Claude
            ? ClaudeStreamParser.Parse(line, isError)
            : CodexJsonlParser.Parse(line, isError);
        foreach (var e in events)
        {
            Lines.Add(e);
        }

        while (Lines.Count > MaxLines)
        {
            Lines.RemoveAt(0);
        }
    }
}
