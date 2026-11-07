using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using DotDeck.App.Infrastructure.Windows;
using DotDeck.Core.Settings;

namespace DotDeck.App.Views.Dialogs;

public partial class SettingsDialog : Window
{
    public SettingsDialog(AppSettings current)
    {
        InitializeComponent();
        DarkTitleBar.Attach(this);
        ThemeBox.SelectedIndex = current.Theme == "Light" ? 1 : 0;
        FontSizeBox.Text = current.EditorFontSize.ToString("0.##", CultureInfo.InvariantCulture);
        RetentionBox.Text = current.BackupRetention.ToString(CultureInfo.InvariantCulture);
        Result = current;
    }

    public AppSettings Result { get; private set; }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(FontSizeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var size)
            || size < AppSettings.MinFontSize || size > AppSettings.MaxFontSize)
        {
            ShowError($"글꼴 크기는 {AppSettings.MinFontSize}~{AppSettings.MaxFontSize} 사이 숫자여야 합니다.");
            return;
        }

        if (!int.TryParse(RetentionBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var retention) || retention is < 1 or > 1000)
        {
            ShowError("백업 보관 개수는 1~1000 사이 정수여야 합니다.");
            return;
        }

        Result = new AppSettings
        {
            Theme = (ThemeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Dark",
            EditorFontSize = size,
            BackupRetention = retention,
        };
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
