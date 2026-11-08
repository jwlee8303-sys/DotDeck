using System.Windows;
using System.Windows.Controls;
using DotDeck.App.Infrastructure.Windows;
using DotDeck.App.ViewModels.Dialogs;

namespace DotDeck.App.Views.Dialogs;

/// <summary>A message with several labelled answers and an optional "apply to all" check box.</summary>
public partial class ChoiceDialog : Window
{
    public ChoiceDialog(string title, string message, IReadOnlyList<DialogChoice> choices, string? applyToAllLabel)
    {
        InitializeComponent();
        DarkTitleBar.Attach(this);
        Title = title;
        MessageText.Text = message;
        if (applyToAllLabel is not null)
        {
            ApplyToAllBox.Content = applyToAllLabel;
            ApplyToAllBox.Visibility = Visibility.Visible;
        }

        foreach (var choice in choices)
        {
            var button = new Button
            {
                Content = choice.Label,
                MinWidth = 80,
                Padding = new Thickness(8, 1, 8, 1),
                Margin = new Thickness(Buttons.Children.Count == 0 ? 0 : 8, 0, 0, 0),
                IsDefault = choice.IsDefault,
                IsCancel = choice.IsCancel,
            };
            button.Click += (_, _) =>
            {
                Chosen = choice.Id;
                DialogResult = true;
            };
            Buttons.Children.Add(button);
            if (choice.IsDefault)
            {
                Loaded += (_, _) => button.Focus();
            }
        }
    }

    /// <summary>The chosen <see cref="DialogChoice.Id"/>; null when the window was closed.</summary>
    public string? Chosen { get; private set; }

    public bool ApplyToAll => ApplyToAllBox.IsChecked == true;
}
