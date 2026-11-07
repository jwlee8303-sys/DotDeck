using CommunityToolkit.Mvvm.ComponentModel;

namespace DotDeck.App.ViewModels.Documents;

/// <summary>
/// Unsaved in-memory text document. Placeholder until the AvalonEdit-based editor lands in Phase 1c;
/// lets the docking behaviour (tab groups, floating) be exercised now.
/// </summary>
public sealed partial class ScratchDocumentViewModel : DocumentViewModel
{
    public ScratchDocumentViewModel(int number, Action<DocumentViewModel> close)
        : base($"doc:scratch:{number}", $"제목 없음 {number}", close)
    {
    }

    [ObservableProperty]
    private string _text = string.Empty;
}
