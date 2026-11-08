using System.Windows;
using System.Windows.Controls;
using DotDeck.App.ViewModels.Editor;

namespace DotDeck.App.Views.Editor;
public partial class AdvancedTomlView : UserControl
{
    public AdvancedTomlView() => InitializeComponent();
    private void OnSelection(object sender, RoutedPropertyChangedEventArgs<object> e)
    { if (DataContext is AdvancedTomlViewModel model) model.Selected = e.NewValue as AdvancedTomlNode; }
}
