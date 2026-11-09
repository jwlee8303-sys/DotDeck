using System.Windows;
using DotDeck.App.ViewModels.Dialogs;
namespace DotDeck.App.Views.Dialogs;

public partial class ExclusionSettingsDialog : Window
{
    public ExclusionSettingsDialog(ExclusionSettingsViewModel model)
    {
        InitializeComponent(); DataContext = model;
        model.Saved += OnSaved;
        Closed += (_, _) => model.Saved -= OnSaved;
    }
    private void OnSaved(object? sender, EventArgs e) => DialogResult = true;
}
