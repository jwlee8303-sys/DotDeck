using System.Windows;
using DotDeck.App.Infrastructure.Windows;
using DotDeck.App.ViewModels.Dialogs;

namespace DotDeck.App.Views.Dialogs;

public partial class SessionLaunchDialog : Window
{
    private readonly SessionLaunchViewModel _viewModel;

    public SessionLaunchDialog(SessionLaunchViewModel viewModel)
    {
        InitializeComponent();
        DarkTitleBar.Attach(this);
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    /// <summary>The validated request when the dialog was accepted.</summary>
    public DotDeck.Core.AiCli.Sessions.AiSessionStartRequest? Request { get; private set; }

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.TryBuildRequest() is { } request)
        {
            Request = request;
            DialogResult = true;
        }
    }
}
