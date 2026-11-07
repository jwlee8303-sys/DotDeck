using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DotDeck.App.ViewModels.Editor;

namespace DotDeck.App.Views.Editor;

/// <summary>Form templates; the ▾ buttons open a menu of suggestions (Tag) for a row or a command.</summary>
public partial class FormTemplates : ResourceDictionary
{
    public FormTemplates() => InitializeComponent();

    private void OnDropButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: IEnumerable items } button)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        foreach (var item in items.OfType<string>())
        {
            var menuItem = new MenuItem { Header = new TextBlock { Text = item } };
            menuItem.Click += (_, _) => Choose(button.CommandParameter, item);
            menu.Items.Add(menuItem);
        }

        menu.IsOpen = true;
    }

    private static void Choose(object? target, string value)
    {
        switch (target)
        {
            case ICommand command when command.CanExecute(value):
                command.Execute(value);
                break;
            case PermissionRow permission:
                permission.Kind = value;
                break;
            case HookRow hook:
                hook.Event = value;
                break;
        }
    }
}
