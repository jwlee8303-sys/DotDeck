using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DotDeck.App.Infrastructure.Windows;
using DotDeck.App.ViewModels.Dialogs;
using DotDeck.Core.Explorer.Visibility;
using DotDeck.Core.Settings;

namespace DotDeck.App.Views.Dialogs;

public partial class SettingsDialog : Window
{
    private readonly string _defaultBackupDirectory;
    private readonly ExtensionChoicesViewModel _extensions;

    public SettingsDialog(AppSettings current, string defaultBackupDirectory)
    {
        _defaultBackupDirectory = defaultBackupDirectory;
        InitializeComponent();
        MaskSecretsBox.IsChecked = current.MaskSecretsByDefault;
        DarkTitleBar.Attach(this);
        ThemeBox.SelectedIndex = current.Theme == "Light" ? 1 : 0;
        FontSizeBox.Text = current.EditorFontSize.ToString("0.##", CultureInfo.InvariantCulture);
        RetentionBox.Text = current.BackupRetention.ToString(CultureInfo.InvariantCulture);
        BackupDirBox.Text = current.BackupDirectory ?? defaultBackupDirectory;
        _extensions = new ExtensionChoicesViewModel(current.DefaultExtensions, ExtensionRules.ConfigDocsPreset, showAllFiles: false, isProject: false);
        ExtensionsView.DataContext = _extensions;
        Result = current;
    }

    private void OnBrowseBackupClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "백업 폴더" };
        if (Directory.Exists(BackupDirBox.Text))
        {
            dialog.InitialDirectory = BackupDirBox.Text;
        }

        if (dialog.ShowDialog(this) == true)
        {
            BackupDirBox.Text = dialog.FolderName;
        }
    }

    private void OnDefaultBackupClick(object sender, RoutedEventArgs e) => BackupDirBox.Text = _defaultBackupDirectory;

    public AppSettings Result { get; private set; }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(FontSizeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var size)
            || !double.IsFinite(size) || size < AppSettings.MinFontSize || size > AppSettings.MaxFontSize)
        {
            ShowError($"글꼴 크기는 {AppSettings.MinFontSize}~{AppSettings.MaxFontSize} 사이 숫자여야 합니다.");
            return;
        }

        if (!int.TryParse(RetentionBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var retention) || retention is < 1 or > 1000)
        {
            ShowError("백업 보관 개수는 1~1000 사이 정수여야 합니다.");
            return;
        }

        var backupDir = BackupDirBox.Text.Trim();
        if (!Path.IsPathFullyQualified(backupDir))
        {
            ShowError("백업 폴더는 전체 경로여야 합니다.");
            return;
        }

        try
        {
            Directory.CreateDirectory(backupDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            ShowError($"백업 폴더를 만들 수 없습니다: {ex.Message}");
            return;
        }

        Result = Result with
        {
            BackupDirectory = string.Equals(Path.GetFullPath(backupDir).TrimEnd('\\'), Path.GetFullPath(_defaultBackupDirectory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                ? null
                : backupDir,
        };
        var extensions = _extensions.Checked;
        if (extensions.Count == 0)
        {
            ShowError("기본 확장자를 하나 이상 선택하세요.");
            return;
        }

        Result = Result with
        {
            // The preset itself is stored as "no choice" so a future preset update applies.
            DefaultExtensions = extensions.SequenceEqual(ExtensionRules.NormalizeAll(ExtensionRules.ConfigDocsPreset), StringComparer.Ordinal) ? null : extensions,
            Theme = (ThemeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Dark",
            EditorFontSize = size,
            BackupRetention = retention,
            MaskSecretsByDefault = MaskSecretsBox.IsChecked == true,
        };
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
