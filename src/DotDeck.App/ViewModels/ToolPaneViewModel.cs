using CommunityToolkit.Mvvm.ComponentModel;

namespace DotDeck.App.ViewModels;

public enum ToolLocation
{
    Left,
    Right,
    Bottom,
}

/// <summary>Dockable tool window. Closing a tool hides it; it can be shown again from the View menu.</summary>
public abstract partial class ToolPaneViewModel : PaneViewModel
{
    protected ToolPaneViewModel(string contentId, string title, ToolLocation preferredLocation)
        : base(contentId, title)
    {
        PreferredLocation = preferredLocation;
    }

    /// <summary>Where the tool is placed when it is first inserted into a fresh layout.</summary>
    public ToolLocation PreferredLocation { get; }

    [ObservableProperty]
    private bool _isVisible = true;

    partial void OnIsVisibleChanged(bool value) => OnVisibilityChanged(value);

    /// <summary>Hook for derived panes (e.g. a detached explorer reattaches when hidden).</summary>
    protected virtual void OnVisibilityChanged(bool isVisible)
    {
    }
}
