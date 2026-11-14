using AvalonDock.Layout;
using DotDeck.App.ViewModels;
using DotDeck.App.ViewModels.Explorer;
using DotDeck.App.ViewModels.Tools;

namespace DotDeck.App.Docking;

/// <summary>
/// Places panes inserted from the view model. While the app builds or restores a layout, tool panes go to their
/// preferred side pane. Afterwards a new pane opens next to the active (or last active) pane of the same kind, or
/// where that kind last was; the first pane of a kind opens in the tab group of the project area.
/// </summary>
public sealed class LayoutInitializer : ILayoutUpdateStrategy
{
    private readonly Dictionary<string, WeakReference<object>> _lastContent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WeakReference<ILayoutPane>> _lastPane = new(StringComparer.Ordinal);

    /// <summary>True while a layout is built or restored: tools go to their preferred pane, documents to the default.</summary>
    public bool UsePreferredLocations { get; set; } = true;

    /// <summary>Content the next pane should open next to (e.g. the terminal whose "+" was clicked); wins over history.</summary>
    public Func<object?>? ExplicitAnchor { get; set; }

    /// <summary>Records the active pane so later panes of its kind open next to it (or where it was, once closed).</summary>
    public void NoteActive(LayoutContent? content)
    {
        if (content?.Content is not { } model || content.Parent is not ILayoutPane pane || !IsTabGroup(pane))
        {
            return;
        }

        var kind = PaneKinds.Of(model);
        _lastContent[kind] = new WeakReference<object>(model);
        _lastPane[kind] = new WeakReference<ILayoutPane>(pane);
    }

    public bool BeforeInsertAnchorable(LayoutRoot layout, LayoutAnchorable anchorableToShow, ILayoutContainer destinationContainer)
    {
        // A hidden tool being shown again remembers where it was (PreviousContainer): let AvalonDock put it
        // back so user-chosen positions survive hide/show.
        // (destinationContainer is non-null on first insertion too, so it cannot be used to tell them apart.)
        if (anchorableToShow is ILayoutPreviousContainer { PreviousContainer: not null }
            || anchorableToShow.Content is not ToolPaneViewModel tool)
        {
            return false;
        }

        if (!UsePreferredLocations && Place(layout, anchorableToShow))
        {
            return true;
        }

        if (PreferredPane(layout, tool.PreferredLocation) is not { } pane)
        {
            return false;
        }

        pane.Children.Add(anchorableToShow);
        return true;
    }

    public void AfterInsertAnchorable(LayoutRoot layout, LayoutAnchorable anchorableShown)
    {
    }

    public bool BeforeInsertDocument(LayoutRoot layout, LayoutDocument anchorableToShow, ILayoutContainer destinationContainer) =>
        !UsePreferredLocations && Place(layout, anchorableToShow);

    public void AfterInsertDocument(LayoutRoot layout, LayoutDocument anchorableShown)
    {
    }

    /// <summary>Applies the kind rules; false leaves the item to the default placement.</summary>
    internal bool Place(LayoutRoot layout, LayoutContent item)
    {
        var kind = PaneKinds.Of(item.Content);

        // 1. Next to the requested pane, the last active pane of this kind, or the most recently used one still open.
        var anchors = new[] { ExplicitAnchor?.Invoke(), Last(_lastContent, kind) }
            .Where(a => a is not null && PaneKinds.Of(a) == kind)
            .Select(a => Find(layout, a!))
            .Concat(layout.Descendents().OfType<LayoutContent>()
                .Where(c => c != item && PaneKinds.Of(c.Content) == kind)
                .OrderByDescending(c => c.LastActivationTimeStamp ?? DateTime.MinValue));
        foreach (var anchor in anchors)
        {
            if (anchor?.Parent is ILayoutPane pane and ILayoutGroup group && IsTabGroup(pane) && TryInsert(pane, item, group.IndexOfChild(anchor) + 1))
            {
                return true;
            }
        }

        // 2. Where this kind last was (its panes are closed now but the tab group remains).
        if (Last(_lastPane, kind) is { } lastPane && lastPane.Root == layout && TryInsert(lastPane, item, lastPane.ChildrenCount))
        {
            return true;
        }

        // 3. First of its kind: the tab group of the project area, or for documents the main document area.
        if (layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(a => a.Content is ProjectsPaneViewModel) is { IsHidden: false, Parent: ILayoutPane projects }
            && IsTabGroup(projects) && TryInsert(projects, item, projects.ChildrenCount))
        {
            return true;
        }

        return item is LayoutDocument
            && layout.RootPanel?.Descendents().OfType<LayoutDocumentPane>().FirstOrDefault() is { } documents
            && TryInsert(documents, item, documents.ChildrenCount);
    }

    private static LayoutAnchorablePane? PreferredPane(LayoutRoot layout, ToolLocation location)
    {
        var paneName = location switch
        {
            ToolLocation.Left => DefaultLayout.LeftPaneName,
            ToolLocation.Right => DefaultLayout.RightPaneName,
            _ => DefaultLayout.BottomPaneName,
        };

        return layout.Descendents().OfType<LayoutAnchorablePane>().FirstOrDefault(p => p.Name == paneName);
    }

    private static LayoutContent? Find(LayoutRoot layout, object content) =>
        layout.Descendents().OfType<LayoutContent>().FirstOrDefault(c => ReferenceEquals(c.Content, content));

    private static T? Last<T>(Dictionary<string, WeakReference<T>> map, string kind) where T : class =>
        map.TryGetValue(kind, out var reference) && reference.TryGetTarget(out var target) ? target : null;

    // Tab groups only: an auto-hide strip or the hidden list is not a place to open a pane.
    private static bool IsTabGroup(ILayoutPane pane) => pane is LayoutDocumentPane or LayoutAnchorablePane;

    // A document pane hosts documents and tools (tabbed); a tool pane hosts tools only.
    private static bool TryInsert(ILayoutPane pane, LayoutContent item, int index)
    {
        switch (pane, item)
        {
            case (LayoutDocumentPane documents, _):
                documents.Children.Insert(Math.Clamp(index, 0, documents.Children.Count), item);
                return true;
            case (LayoutAnchorablePane tools, LayoutAnchorable tool):
                tools.Children.Insert(Math.Clamp(index, 0, tools.Children.Count), tool);
                return true;
            default:
                return false;
        }
    }
}

/// <summary>Pane kinds for placement: every document is a "file" whatever its extension; each tool kind is its own.</summary>
public static class PaneKinds
{
    public const string File = "file";

    public const string Terminal = "terminal";

    public const string Explorer = "explorer";

    public static string Of(object? content) => content switch
    {
        DocumentViewModel => File,
        TerminalPaneViewModel => Terminal,
        DetachedExplorerPaneViewModel => Explorer,
        null => string.Empty,
        _ => content.GetType().FullName ?? content.GetType().Name,
    };
}
