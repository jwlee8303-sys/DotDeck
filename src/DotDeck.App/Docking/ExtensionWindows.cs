using System.Globalization;
using System.Windows;
using AvalonDock;
using AvalonDock.Layout;
using DotDeck.App.ViewModels;

namespace DotDeck.App.Docking;

/// <summary>
/// Extension windows: floating docking windows of the main window that hold files, terminals and other panes.
/// A new one starts with a guide tab; panes are dragged in or moved with "확장 창으로 이동".
/// </summary>
public sealed class ExtensionWindows
{
    private const double DefaultWidth = 900;
    private const double DefaultHeight = 600;

    private readonly DockingManager _manager;
    private readonly ShellViewModel _shell;

    public ExtensionWindows(DockingManager manager, ShellViewModel shell)
    {
        _manager = manager;
        _shell = shell;
    }

    /// <summary>Open extension windows in creation order (document floating windows that still hold a tab group).</summary>
    public IReadOnlyList<LayoutDocumentFloatingWindow> List() =>
        _manager.Layout.FloatingWindows.OfType<LayoutDocumentFloatingWindow>()
            .Where(w => TargetPane(w) is not null)
            .ToList();

    /// <summary>Menu text of a window: its number and the tab shown in it.</summary>
    public static string NameOf(LayoutDocumentFloatingWindow window, int index)
    {
        var shown = window.Descendents().OfType<LayoutContent>().FirstOrDefault(c => c.IsSelected)
            ?? window.Descendents().OfType<LayoutContent>().FirstOrDefault();
        var number = string.Create(CultureInfo.InvariantCulture, $"확장 창 {index + 1}");
        return shown is null ? number : $"{number} — {shown.Title}";
    }

    /// <summary>Opens a new extension window; with <paramref name="move"/>, that pane moves into it.</summary>
    public LayoutDocumentFloatingWindow? Create(LayoutContent? move = null)
    {
        var guide = _shell.AddExtensionWindowDocument();
        if (Find(guide) is not LayoutDocument document)
        {
            return null;
        }

        // Cascade new windows over the main window so they never open off-screen.
        var owner = Window.GetWindow(_manager);
        var offset = 40 * (List().Count + 1);
        document.FloatingWidth = DefaultWidth;
        document.FloatingHeight = DefaultHeight;
        if (owner is not null)
        {
            document.FloatingLeft = owner.Left + offset;
            document.FloatingTop = owner.Top + offset;
        }

        document.Float();
        if (document.FindParent<LayoutDocumentFloatingWindow>() is not { } window)
        {
            return null;
        }

        if (move is not null && MoveTo(move, window))
        {
            // The moved pane fills the new window; the guide is only for an empty one.
            _shell.Documents.Remove(guide);
        }
        else
        {
            document.IsActive = true;
        }

        return window;
    }

    /// <summary>Moves a pane (file, terminal, tool) into an extension window as a tab and activates it.</summary>
    public bool MoveTo(LayoutContent content, LayoutDocumentFloatingWindow window)
    {
        if (TargetPane(window) is not { } pane)
        {
            return false;
        }

        if (!ReferenceEquals(content.Parent, pane))
        {
            if (content.Parent is not ILayoutContainer parent)
            {
                return false;
            }

            parent.RemoveChild(content);
            pane.Children.Add(content);
        }

        content.IsSelected = true;
        content.IsActive = true;
        return true;
    }

    /// <summary>The layout item of a view model, or null when it is not in the layout.</summary>
    public LayoutContent? Find(object model) =>
        _manager.Layout.Descendents().OfType<LayoutContent>().FirstOrDefault(c => ReferenceEquals(c.Content, model));

    // The tab group showing the selected tab, else the first one of the window.
    private static LayoutDocumentPane? TargetPane(LayoutDocumentFloatingWindow window)
    {
        var panes = window.Descendents().OfType<LayoutDocumentPane>().ToList();
        return panes.FirstOrDefault(p => p.Children.Any(c => c.IsSelected && c.IsActive))
            ?? panes.FirstOrDefault(p => p.ChildrenCount > 0)
            ?? panes.FirstOrDefault();
    }
}
