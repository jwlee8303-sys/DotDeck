using System.Windows;
using System.Windows.Controls;
using DotDeck.App.Infrastructure.Windows;

namespace DotDeck.App.Views.Dialogs;

/// <summary>Single-line text prompt with live validation (e.g. a layout name).</summary>
public partial class TextInputDialog : Window
{
    private readonly Func<string, string?> _validate;

    public TextInputDialog(string title, string prompt, string initial, Func<string, string?> validate)
    {
        _validate = validate;
        InitializeComponent();
        DarkTitleBar.Attach(this);
        Title = title;
        PromptText.Text = prompt;
        InputBox.Text = initial;
        InputBox.SelectAll();
        Validate();
    }

    public string Value => InputBox.Text.Trim();

    private void OnTextChanged(object sender, TextChangedEventArgs e) => Validate();

    private bool Validate()
    {
        var error = InputBox.Text.Length == 0 ? null : _validate(InputBox.Text);
        ErrorText.Text = error ?? string.Empty;
        ErrorText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        OkButton.IsEnabled = InputBox.Text.Trim().Length > 0 && error is null;
        return OkButton.IsEnabled;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (Validate())
        {
            DialogResult = true;
        }
    }
}
