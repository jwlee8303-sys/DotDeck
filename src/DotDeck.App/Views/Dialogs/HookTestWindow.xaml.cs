using System.Windows;
using DotDeck.App.Infrastructure.Windows;
using DotDeck.App.ViewModels.Tools;

namespace DotDeck.App.Views.Dialogs;

/// <summary>Runs one hook (훅 테스트) from a modal dialog, where the main window's pane cannot be used.</summary>
public partial class HookTestWindow : Window
{
    public HookTestWindow(HookTestPaneViewModel test)
    {
        ArgumentNullException.ThrowIfNull(test);
        InitializeComponent();
        DarkTitleBar.Attach(this);
        Pane.Content = test;
        Closing += (_, _) =>
        {
            if (test.StopCommand.CanExecute(null))
            {
                test.StopCommand.Execute(null); // a running hook is not left behind
            }
        };
    }
}
