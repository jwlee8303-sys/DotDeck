using CommunityToolkit.Mvvm.Input;

namespace DotDeck.App.ViewModels;

/// <summary>Document tab. The shell supplies the close callback so documents stay unaware of the shell.</summary>
public abstract partial class DocumentViewModel : PaneViewModel
{
    private readonly Action<DocumentViewModel> _close;

    protected DocumentViewModel(string contentId, string title, Action<DocumentViewModel> close)
        : base(contentId, title)
    {
        _close = close;
    }

    /// <summary>Whether the document can be recreated from its ContentId when a saved layout is restored.</summary>
    public virtual bool IsRestorable => false;

    [RelayCommand]
    private void Close() => _close(this);
}
