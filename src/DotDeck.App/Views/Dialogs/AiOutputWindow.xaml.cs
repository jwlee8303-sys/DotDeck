using System.Windows;
using DotDeck.App.Infrastructure.Windows;
using DotDeck.App.ViewModels.Dialogs;

namespace DotDeck.App.Views.Dialogs;

/// <summary>Modeless window with a non-interactive AI session's output. Closing it stops following the session.</summary>
public partial class AiOutputWindow : Window
{
    public AiOutputWindow(AiSessionOutputViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        DarkTitleBar.Attach(this);
        Closed += (_, _) => viewModel.Detach();
    }
}
