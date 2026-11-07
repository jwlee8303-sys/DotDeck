using AvalonDock.Layout;
using DotDeck.App.ViewModels;

namespace DotDeck.App.Docking;

/// <summary>Places tool panes into their preferred side pane when they are inserted from the view model.</summary>
public sealed class LayoutInitializer : ILayoutUpdateStrategy
{
    public bool BeforeInsertAnchorable(LayoutRoot layout, LayoutAnchorable anchorableToShow, ILayoutContainer destinationContainer)
    {
        // A hidden tool being shown again remembers where it was (PreviousContainer): let AvalonDock put it
        // back so user-chosen positions survive hide/show. Preferred locations apply to first insertion only.
        // (destinationContainer is non-null on first insertion too, so it cannot be used to tell them apart.)
        if (anchorableToShow is ILayoutPreviousContainer { PreviousContainer: not null }
            || anchorableToShow.Content is not ToolPaneViewModel tool)
        {
            return false;
        }

        var paneName = tool.PreferredLocation switch
        {
            ToolLocation.Left => DefaultLayout.LeftPaneName,
            ToolLocation.Right => DefaultLayout.RightPaneName,
            _ => DefaultLayout.BottomPaneName,
        };

        var pane = layout.Descendents()
            .OfType<LayoutAnchorablePane>()
            .FirstOrDefault(p => p.Name == paneName);
        if (pane is null)
        {
            return false;
        }

        pane.Children.Add(anchorableToShow);
        return true;
    }

    public void AfterInsertAnchorable(LayoutRoot layout, LayoutAnchorable anchorableShown)
    {
    }

    public bool BeforeInsertDocument(LayoutRoot layout, LayoutDocument anchorableToShow, ILayoutContainer destinationContainer) => false;

    public void AfterInsertDocument(LayoutRoot layout, LayoutDocument anchorableShown)
    {
    }
}
