namespace DotDeck.Core.AiCli.Approvals;

/// <summary>"환경 설정 → AI 승인 요청" (stored as <c>AiApproval</c> in settings.json).</summary>
public sealed record ApprovalSettings
{
    /// <summary>
    /// How long DotDeck waits for an answer to a hook request before handing it to the CLI prompt. Longer than Claude
    /// Code's default hook timeout (600 s), so the hook's timeout is written explicitly
    /// (<see cref="ClaudeApprovalHook.HookTimeoutSeconds"/>).
    /// </summary>
    public static readonly TimeSpan HookWaitLimit = TimeSpan.FromMinutes(30);

    /// <summary>"승인 요청 감지 사용".</summary>
    public bool Enabled { get; init; } = true;

    public bool DetectClaude { get; init; } = true;

    public bool DetectCodex { get; init; } = true;

    public bool DetectAgy { get; init; } = true;

    /// <summary>"대체 감지(화면 패턴) 사용".</summary>
    public bool ScreenDetection { get; init; } = true;

    /// <summary>"승인 대기 세션을 목록 맨 위로".</summary>
    public bool PinWaitingToTop { get; init; } = true;

    /// <summary>"비활성·트레이 상태에서 알림 카드 표시".</summary>
    public bool NotificationCards { get; init; } = true;

    public bool NotificationSound { get; init; }

    public bool Detects(Launch.CliKind cli) => Enabled && cli switch
    {
        Launch.CliKind.Claude => DetectClaude,
        Launch.CliKind.Codex => DetectCodex,
        _ => DetectAgy,
    };
}
