using DotDeck.App.ViewModels.Documents;
using DotDeck.App.ViewModels.Tools;

namespace DotDeck.App.ViewModels;

/// <summary>
/// Tab menu "이 창을 제외하고 모두 닫기" / "모두 닫기": closes the tabs of one tab group, whatever their kind (documents,
/// terminals, tool panes). The view collects the group; the shell decides how each kind closes.
/// </summary>
public sealed partial class ShellViewModel
{
    /// <summary>Whether the tab menu may close <paramref name="pane"/> (a pinned document stays).</summary>
    public static bool CanCloseTab(PaneViewModel pane) => pane switch
    {
        TextDocumentViewModel text => text.CanClose,
        DocumentViewModel or ToolPaneViewModel => true,
        _ => false,
    };

    /// <summary>
    /// Closes <paramref name="panes"/>: documents as by their close button (unsaved changes are asked about one by one),
    /// terminals after one confirmation for all running ones, other tool panes are hidden.
    /// </summary>
    public void CloseTabs(IReadOnlyList<PaneViewModel> panes)
    {
        ArgumentNullException.ThrowIfNull(panes);
        var closable = panes.Where(CanCloseTab).ToList();

        var terminals = closable.OfType<TerminalPaneViewModel>().Where(Tools.Contains).ToList();
        var running = terminals.Count(t => t.IsRunning);
        if (running > 0 && !_dialogs.Confirm(
                "터미널",
                $"터미널 {running}개가 실행 중입니다.\n\n탭을 닫으면 터미널과 그 안에서 실행한 프로그램(AI 세션 포함)이 모두 종료됩니다. 닫을까요?"))
        {
            terminals.Clear();
        }

        foreach (var terminal in terminals)
        {
            CloseTerminal(terminal);
        }

        foreach (var tool in closable.OfType<ToolPaneViewModel>().Where(t => t is not TerminalPaneViewModel))
        {
            tool.IsVisible = false;
        }

        foreach (var document in closable.OfType<DocumentViewModel>())
        {
            document.CloseCommand.Execute(null);
        }
    }
}
