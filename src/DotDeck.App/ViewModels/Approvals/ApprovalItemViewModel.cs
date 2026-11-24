using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotDeck.Core.AiCli.Approvals;
using DotDeck.Core.AiCli.Launch;

namespace DotDeck.App.ViewModels.Approvals;

/// <summary>What the approval views can ask for; everything goes through <see cref="ApprovalService"/> once.</summary>
public interface IApprovalActions
{
    Task RespondAsync(string requestId, ApprovalDecision decision);

    /// <summary>"터미널로 이동" (a waiting hook request is then handed to the terminal prompt).</summary>
    void GoToTerminal(string sessionKey);

    /// <summary>"열기" on a card: restore DotDeck and show the session in AI 모니터.</summary>
    void Open(string sessionKey);

    /// <summary>"×" on a card: the card closes, the request stays in AI 모니터.</summary>
    void DismissCard(string requestId);
}

/// <summary>
/// The approval state of one session (AI 모니터 section, notification card): the current request with its command,
/// tool, folder and waiting time, or the last result. Refreshed by the coordinator.
/// </summary>
public sealed partial class ApprovalItemViewModel : ObservableObject
{
    /// <summary>Longer commands are cut in the one-line summary (the full text is behind "펼치기").</summary>
    public const int SummaryLength = 90;

    private readonly IApprovalActions _actions;
    private DateTimeOffset _now;

    public ApprovalItemViewModel(string sessionKey, string sessionTitle, IApprovalActions actions)
    {
        SessionKey = sessionKey;
        SessionTitle = sessionTitle;
        _actions = actions;
    }

    public string SessionKey { get; }

    /// <summary>Name shown on a notification card (session or terminal tab name).</summary>
    [ObservableProperty]
    private string _sessionTitle;

    /// <summary>The request shown (waiting, or handed to the terminal), or null.</summary>
    public ApprovalRequest? Request { get; private set; }

    /// <summary>The last finished request ("허용됨 · 21:30").</summary>
    public ApprovalRequest? Last { get; private set; }

    /// <summary>Waiting requests of the session besides the shown one.</summary>
    public int MoreWaiting { get; private set; }

    /// <summary>The full command is shown (the "펼치기" toggle).</summary>
    [ObservableProperty]
    private bool _isExpanded;

    public bool IsWaiting => Request?.IsWaiting == true;

    public bool IsHandedOff => Request?.State == ApprovalState.HandedToTerminal;

    public bool HasRequest => Request is not null;

    public bool HasLastResult => Request is null && Last is not null;

    public string LastResultText => Last is { } last ? ApprovalService.FormatResult(last) : string.Empty;

    public string CliText => (Request ?? Last)?.Cli switch
    {
        CliKind.Claude => "Claude",
        CliKind.Codex => "Codex",
        CliKind.Antigravity => "agy",
        _ => string.Empty,
    };

    public string ToolText => Request?.Source == ApprovalSource.Screen ? "화면 감지 · " + Request.ToolName : Request?.ToolName ?? string.Empty;

    public string CommandText => Request?.Command ?? string.Empty;

    public string CommandSummary
    {
        get
        {
            var text = CommandText.ReplaceLineEndings(" ").Trim();
            return text.Length > SummaryLength ? text[..SummaryLength] + "…" : text;
        }
    }

    public bool IsCommandLong => CommandText.Length > SummaryLength || CommandText.Contains('\n', StringComparison.Ordinal);

    public string FolderText => Request?.WorkingDirectory ?? string.Empty;

    public bool HasFolder => FolderText.Length > 0;

    /// <summary>"⚠ 승인 대기 2:03" (or "터미널에서 응답 중").</summary>
    public string WaitingText => Request switch
    {
        null => string.Empty,
        { State: ApprovalState.HandedToTerminal } r => r.ResultText ?? "터미널에서 응답 중",
        { State: ApprovalState.Responding } => "응답 보내는 중…",
        { Source: ApprovalSource.Screen } r => "⚠ 승인 대기(추정) " + Clock(_now - r.RequestedAt),
        { } r => "⚠ 승인 대기 " + Clock(_now - r.RequestedAt),
    };

    /// <summary>Why DotDeck cannot answer (only [터미널로 이동] is offered), or null.</summary>
    public string? NoteText => Request switch
    {
        { State: ApprovalState.Pending, CanRespond: false } r => r.CannotRespondReason ?? "DotDeck에서 응답할 수 없습니다 · 터미널에서 응답하세요",
        _ => null,
    };

    public bool HasNote => NoteText is not null;

    public bool CanAnswer => Request is { State: ApprovalState.Pending, CanRespond: true };

    public string? QueueText => MoreWaiting > 0 ? string.Create(CultureInfo.InvariantCulture, $"이 세션에 {MoreWaiting}건 더 대기") : null;

    internal void Update(ApprovalRequest? request, ApprovalRequest? last, int moreWaiting, DateTimeOffset now)
    {
        if (!ReferenceEquals(Request, request))
        {
            IsExpanded = false;
        }

        Request = request;
        Last = last;
        MoreWaiting = moreWaiting;
        _now = now;
        OnPropertyChanged(string.Empty);
        AllowCommand.NotifyCanExecuteChanged();
        DenyCommand.NotifyCanExecuteChanged();
    }

    // The id is captured when the button is pressed: the answer applies to that request only.
    [RelayCommand(CanExecute = nameof(CanAnswer))]
    private Task Allow() => Request is { } r ? _actions.RespondAsync(r.Id, ApprovalDecision.Allow) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanAnswer))]
    private Task Deny() => Request is { } r ? _actions.RespondAsync(r.Id, ApprovalDecision.Deny) : Task.CompletedTask;

    [RelayCommand]
    private void GoToTerminal() => _actions.GoToTerminal(SessionKey);

    [RelayCommand]
    private void Open() => _actions.Open(SessionKey);

    [RelayCommand]
    private void DismissCard()
    {
        if (Request is { } r)
        {
            _actions.DismissCard(r.Id);
        }
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    internal static string Clock(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{span.Minutes}:{span.Seconds:00}");
    }
}
