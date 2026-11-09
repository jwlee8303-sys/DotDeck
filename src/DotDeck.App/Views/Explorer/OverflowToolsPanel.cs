using System.Windows;
using System.Windows.Controls;

namespace DotDeck.App.Views.Explorer;

/// <summary>
/// Lays out a title and a tool strip on one line, giving the title priority: when the title and the full tool strip
/// do not both fit, the strip is folded away and a single overflow button ("…") takes its place. Children, in order:
/// the title, the full tool strip, the overflow button. Only layout lives here; the overflow button's menu repeats
/// the strip's commands.
/// </summary>
public sealed class OverflowToolsPanel : Panel
{
    public static readonly DependencyProperty IsOverflowingProperty = DependencyProperty.Register(
        nameof(IsOverflowing), typeof(bool), typeof(OverflowToolsPanel), new PropertyMetadata(false));

    /// <summary>True while the tool strip is folded into the overflow button.</summary>
    public bool IsOverflowing
    {
        get => (bool)GetValue(IsOverflowingProperty);
        private set => SetValue(IsOverflowingProperty, value);
    }

    private UIElement? Title => InternalChildren.Count > 0 ? InternalChildren[0] : null;

    private UIElement? Tools => InternalChildren.Count > 1 ? InternalChildren[1] : null;

    private UIElement? Overflow => InternalChildren.Count > 2 ? InternalChildren[2] : null;

    protected override Size MeasureOverride(Size availableSize)
    {
        var unbounded = new Size(double.PositiveInfinity, availableSize.Height);
        Tools?.Measure(unbounded);
        Overflow?.Measure(unbounded);
        Title?.Measure(unbounded);

        var titleWidth = Title?.DesiredSize.Width ?? 0;
        var toolsWidth = Tools?.DesiredSize.Width ?? 0;
        var overflowWidth = Overflow?.DesiredSize.Width ?? 0;
        var overflowing = !double.IsInfinity(availableSize.Width) && titleWidth + toolsWidth > availableSize.Width;
        if (overflowing != IsOverflowing)
        {
            IsOverflowing = overflowing;
        }

        var sideWidth = overflowing ? overflowWidth : toolsWidth;
        if (!double.IsInfinity(availableSize.Width))
        {
            // The title gets what is left (trimmed with an ellipsis).
            Title?.Measure(new Size(Math.Max(0, availableSize.Width - sideWidth), availableSize.Height));
        }

        var height = Math.Max(Title?.DesiredSize.Height ?? 0, Math.Max(Tools?.DesiredSize.Height ?? 0, Overflow?.DesiredSize.Height ?? 0));
        var width = double.IsInfinity(availableSize.Width)
            ? titleWidth + toolsWidth
            : availableSize.Width;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var overflowing = IsOverflowing;
        var side = overflowing ? Overflow : Tools;
        var hidden = overflowing ? Tools : Overflow;
        var sideWidth = Math.Min(side?.DesiredSize.Width ?? 0, finalSize.Width);
        var titleWidth = Math.Max(0, finalSize.Width - sideWidth);

        Title?.Arrange(new Rect(0, 0, titleWidth, finalSize.Height));
        side?.Arrange(new Rect(finalSize.Width - sideWidth, 0, sideWidth, finalSize.Height));

        // Arranged with no size: clipped away, so it neither shows nor takes clicks, and keeps its measure.
        hidden?.Arrange(new Rect(finalSize.Width, 0, 0, 0));
        if (hidden is not null)
        {
            hidden.IsHitTestVisible = false;
        }

        if (side is not null)
        {
            side.IsHitTestVisible = true;
        }

        return finalSize;
    }
}
