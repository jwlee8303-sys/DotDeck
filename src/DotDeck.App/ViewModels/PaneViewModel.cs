using CommunityToolkit.Mvvm.ComponentModel;

namespace DotDeck.App.ViewModels;

/// <summary>Base for anything hosted by the docking manager (tool panes and documents).</summary>
public abstract partial class PaneViewModel : ObservableObject
{
    protected PaneViewModel(string contentId, string title)
    {
        ContentId = contentId;
        _title = title;
    }

    /// <summary>Stable identifier used by layout serialization. Must be unique.</summary>
    public string ContentId { get; }

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isActive;
}
