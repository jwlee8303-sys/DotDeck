using System.Windows;
using System.Windows.Controls;
using DotDeck.App.Views.Explorer;

namespace DotDeck.App.Tests;

public sealed class ExplorerHeaderLayoutTests
{
    private static Task RunOnSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static (OverflowToolsPanel Panel, FrameworkElement Title, FrameworkElement Tools, FrameworkElement Overflow) Create()
    {
        var title = new TextBlock { Text = "Project", Width = 100 };
        var tools = new Border { Width = 120, Height = 20 };
        var overflow = new Border { Width = 20, Height = 20 };
        var panel = new OverflowToolsPanel();
        panel.Children.Add(title);
        panel.Children.Add(tools);
        panel.Children.Add(overflow);
        return (panel, title, tools, overflow);
    }

    private static void Layout(FrameworkElement element, double width)
    {
        element.Measure(new Size(width, 22));
        element.Arrange(new Rect(0, 0, width, 22));
    }

    [Fact]
    public Task Wide_header_shows_the_tools_next_to_the_full_name() => RunOnSta(() =>
    {
        var (panel, title, tools, overflow) = Create();

        Layout(panel, 300);

        Assert.False(panel.IsOverflowing);
        Assert.Equal(100, title.RenderSize.Width);
        Assert.Equal(120, tools.RenderSize.Width);
        Assert.True(tools.IsHitTestVisible);
        Assert.False(overflow.IsHitTestVisible);
    });

    [Fact]
    public Task Narrow_header_folds_the_tools_into_the_overflow_button_and_keeps_the_name() => RunOnSta(() =>
    {
        var (panel, title, tools, overflow) = Create();

        Layout(panel, 150);

        Assert.True(panel.IsOverflowing);
        Assert.Equal(100, title.RenderSize.Width);
        Assert.True(overflow.IsHitTestVisible);
        Assert.False(tools.IsHitTestVisible);

        Layout(panel, 400);

        Assert.False(panel.IsOverflowing);
        Assert.True(tools.IsHitTestVisible);
    });
}
