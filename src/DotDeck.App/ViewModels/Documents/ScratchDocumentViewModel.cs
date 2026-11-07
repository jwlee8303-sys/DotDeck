using ICSharpCode.AvalonEdit.Document;

namespace DotDeck.App.ViewModels.Documents;

/// <summary>
/// Untitled in-memory text document ("새 파일"). Saving asks for a path and moves the text into a normal editor
/// tab for that file (<see cref="ShellViewModel.SaveUntitledAsync"/>).
/// </summary>
public sealed class ScratchDocumentViewModel : DocumentViewModel
{
    private readonly string _name;

    public ScratchDocumentViewModel(int number, Action<DocumentViewModel> close)
        : base($"doc:scratch:{number}", $"제목 없음 {number}", close)
    {
        _name = $"제목 없음 {number}";
        Document.TextChanged += (_, _) =>
        {
            Title = IsDirty ? _name + " *" : _name;
            OnPropertyChanged(nameof(IsDirty));
        };
    }

    /// <summary>Editor content (same document type as file tabs, so the same editor control shows it).</summary>
    public TextDocument Document { get; } = new();

    public string Text => Document.Text;

    /// <summary>Unsaved content worth asking about (an empty untitled tab closes silently).</summary>
    public bool IsDirty => Document.TextLength > 0;

    public string SuggestedFileName => _name + ".md";
}
