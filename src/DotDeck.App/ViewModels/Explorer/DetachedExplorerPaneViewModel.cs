namespace DotDeck.App.ViewModels.Explorer;

/// <summary>
/// A project's explorer shown in its own dockable tool pane (DESIGN 5). Closing the pane reattaches the
/// explorer to the project area; expansion and selection are kept.
/// </summary>
public sealed class DetachedExplorerPaneViewModel : ToolPaneViewModel
{
    public const string ContentIdPrefix = "tool.explorer:";

    private readonly Action<DetachedExplorerPaneViewModel> _onClosed;

    public DetachedExplorerPaneViewModel(string projectId, string title, ExplorerListViewModel list, Action<DetachedExplorerPaneViewModel> onClosed)
        : base(ContentIdPrefix + projectId, title, ToolLocation.Left)
    {
        ProjectId = projectId;
        List = list;
        _onClosed = onClosed;
    }

    public string ProjectId { get; }

    public ExplorerListViewModel List { get; }

    /// <summary>The explorer shown here; null until the project has been loaded.</summary>
    public ExplorerViewModel? Explorer { get; set; }

    /// <summary>Set while the pane is being removed by the app (not by the user closing it).</summary>
    public bool IsClosingByApp { get; set; }

    // Tool panes hide instead of closing; for a detached explorer "hidden" means "give it back".
    protected override void OnVisibilityChanged(bool isVisible)
    {
        if (!isVisible && !IsClosingByApp)
        {
            _onClosed(this);
        }
    }
}
