using CommunityToolkit.Mvvm.Input;

namespace DotDeck.App.ViewModels;

/// <summary>Document tab. The shell supplies the close callback so documents stay unaware of the shell.</summary>
public abstract partial class DocumentViewModel : PaneViewModel
{
    private readonly Action<DocumentViewModel> _close;
    private bool _isWordWrapEnabled;

    protected DocumentViewModel(string contentId, string title, Action<DocumentViewModel> close)
        : base(contentId, title)
    {
        _close = close;
    }

    /// <summary>Whether the document can be recreated from its ContentId when a saved layout is restored.</summary>
    public virtual bool IsRestorable => false;

    [RelayCommand]
    private void Close() => _close(this);

    // ===== Soft wrap ("자동 줄 바꿈", Alt+Z) =====

    /// <summary>
    /// The tab shows text in a text editor right now, so soft wrap applies. Tables, forms and other non-text tabs
    /// return false (the menu item, Alt+Z and the status bar toggle are then disabled).
    /// </summary>
    public virtual bool SupportsWordWrap => false;

    /// <summary>
    /// Long lines are folded at the editor width. Display only: the text, undo history and dirty state are untouched.
    /// </summary>
    public virtual bool IsWordWrapEnabled
    {
        get => _isWordWrapEnabled;
        set
        {
            if (SetProperty(ref _isWordWrapEnabled, value))
            {
                OnPropertyChanged(nameof(WordWrapStatusText));
            }
        }
    }

    /// <summary>Status bar text, e.g. "줄 바꿈: 켬".</summary>
    public string WordWrapStatusText => IsWordWrapEnabled ? "줄 바꿈: 켬" : "줄 바꿈: 끔";

    [RelayCommand(CanExecute = nameof(SupportsWordWrap))]
    private void ToggleWordWrap() => IsWordWrapEnabled = !IsWordWrapEnabled;

    /// <summary>For overrides that keep the state elsewhere: raises the change notifications of the wrap state.</summary>
    protected void OnWordWrapChanged()
    {
        OnPropertyChanged(nameof(IsWordWrapEnabled));
        OnPropertyChanged(nameof(WordWrapStatusText));
    }

    /// <summary>For overrides whose <see cref="SupportsWordWrap"/> changes (e.g. switching to a form view).</summary>
    protected void OnSupportsWordWrapChanged()
    {
        OnPropertyChanged(nameof(SupportsWordWrap));
        ToggleWordWrapCommand.NotifyCanExecuteChanged();
    }
}
