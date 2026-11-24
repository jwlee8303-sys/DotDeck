using DotDeck.App.ViewModels.Approvals;
using DotDeck.App.ViewModels.Tools;

namespace DotDeck.App.ViewModels;

/// <summary>AI approval requests: AI 모니터 sections, terminal bars and the shell actions the cards use.</summary>
public sealed partial class ShellViewModel
{
    /// <summary>Connects the approval coordinator (once, from App startup).</summary>
    /// <param name="probe">Reads the screen of a terminal tab that is not shown (estimate).</param>
    /// <param name="restoreMainWindow">Restores DotDeck from the tray or the minimized state.</param>
    public void AttachApprovals(
        ApprovalCoordinator approvals,
        Func<TerminalPaneViewModel, CancellationToken, Task<string?>> probe,
        Action restoreMainWindow)
    {
        ArgumentNullException.ThrowIfNull(approvals);
        approvals.Attach(new ApprovalShellBridge(
            () => [.. Terminals],
            ShowTerminal,
            ShowAiTerminal,
            AiMonitor.Reveal,
            restoreMainWindow,
            probe));
        AiMonitor.AttachApprovals(approvals);
    }
}
