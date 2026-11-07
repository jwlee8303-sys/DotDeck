using System.Windows;
using System.Windows.Controls;
using DotDeck.App.ViewModels;

namespace DotDeck.App.Docking;

/// <summary>Chooses the AvalonDock container style (title/visibility/close bindings) by view model kind.</summary>
public sealed class PaneStyleSelector : StyleSelector
{
    public Style? ToolStyle { get; set; }

    public Style? DocumentStyle { get; set; }

    public override Style? SelectStyle(object item, DependencyObject container) => item switch
    {
        ToolPaneViewModel => ToolStyle,
        DocumentViewModel => DocumentStyle,
        _ => base.SelectStyle(item, container),
    };
}
