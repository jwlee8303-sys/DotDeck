using System.Windows.Controls;
using AvalonDock.Layout;

namespace DotDeck.App.Docking;

/// <summary>Builds the initial VS-style layout: projects left, documents center, output bottom, properties right.</summary>
public static class DefaultLayout
{
    public const string LeftPaneName = "LeftToolPane";
    public const string RightPaneName = "RightToolPane";
    public const string BottomPaneName = "BottomToolPane";

    public static LayoutRoot Create()
    {
        var left = new LayoutAnchorablePane { Name = LeftPaneName, DockWidth = new System.Windows.GridLength(300) };
        var right = new LayoutAnchorablePane { Name = RightPaneName, DockWidth = new System.Windows.GridLength(280) };
        var bottom = new LayoutAnchorablePane { Name = BottomPaneName, DockHeight = new System.Windows.GridLength(200) };
        var documents = new LayoutDocumentPane();

        var center = new LayoutPanel { Orientation = Orientation.Vertical };
        center.Children.Add(documents);
        center.Children.Add(bottom);

        var root = new LayoutPanel { Orientation = Orientation.Horizontal };
        root.Children.Add(left);
        root.Children.Add(center);
        root.Children.Add(right);

        return new LayoutRoot { RootPanel = root };
    }
}
