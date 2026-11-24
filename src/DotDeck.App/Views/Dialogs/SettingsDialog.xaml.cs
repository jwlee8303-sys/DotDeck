using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DotDeck.App.Infrastructure.Windows;
using DotDeck.App.ViewModels.Dialogs;
using DotDeck.Core.AiCli.Approvals;
using DotDeck.Core.AiCli.ContextBudget;
using DotDeck.Core.Explorer.Visibility;
using DotDeck.Core.Settings;

namespace DotDeck.App.Views.Dialogs;

public partial class SettingsDialog : Window
{
    private readonly string _defaultBackupDirectory;
    private readonly ExtensionChoicesViewModel _extensions;
    private readonly SessionHooksViewModel _sessionHooks;

    public SettingsDialog(AppSettings current, string defaultBackupDirectory, SessionHooksViewModel sessionHooks)
    {
        ArgumentNullException.ThrowIfNull(current);
        _sessionHooks = sessionHooks ?? throw new ArgumentNullException(nameof(sessionHooks));
        _defaultBackupDirectory = defaultBackupDirectory;
        InitializeComponent();
        DarkTitleBar.Attach(this);
        ThemeBox.SelectedIndex = current.Theme == "Light" ? 1 : 0;
        FontSizeBox.Text = current.EditorFontSize.ToString("0.##", CultureInfo.InvariantCulture);
        RetentionBox.Text = current.BackupRetention.ToString(CultureInfo.InvariantCulture);
        BackupDirBox.Text = current.BackupDirectory ?? defaultBackupDirectory;
        CloseToTrayBox.IsChecked = current.CloseToTray;
        var approval = current.AiApproval;
        ApprovalEnabledBox.IsChecked = approval.Enabled;
        DetectClaudeBox.IsChecked = approval.DetectClaude;
        DetectCodexBox.IsChecked = approval.DetectCodex;
        DetectAgyBox.IsChecked = approval.DetectAgy;
        ScreenDetectionBox.IsChecked = approval.ScreenDetection;
        PinWaitingBox.IsChecked = approval.PinWaitingToTop;
        CardsBox.IsChecked = approval.NotificationCards;
        CardSoundBox.IsChecked = approval.NotificationSound;
        var context = current.ContextBudget;
        ContextInstructionBox.Text = context.InstructionFileTokens.ToString(CultureInfo.InvariantCulture);
        ContextShareBox.Text = context.McpSharePercent.ToString(CultureInfo.InvariantCulture);
        ContextDescriptionBox.Text = context.DescriptionTokens.ToString(CultureInfo.InvariantCulture);
        ContextTotalBox.Text = context.TotalWarningTokens.ToString(CultureInfo.InvariantCulture);
        ContextWarningPercentBox.Text = context.UsageWarningPercent.ToString(CultureInfo.InvariantCulture);
        ContextDangerPercentBox.Text = context.UsageDangerPercent.ToString(CultureInfo.InvariantCulture);
        ContextWindowsBox.Text = FormatWindows(context.EffectiveClaudeContextWindows);
        McpDirectMeasureBox.IsChecked = context.McpDirectMeasure;
        SessionHooksTab.DataContext = sessionHooks;
        sessionHooks.ShowHookTest = test => new HookTestWindow(test) { Owner = this }.ShowDialog();
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

        if (ParseContextBudget() is not { } contextBudget)
        {
            return;
        }

        Result = Result with
        {
            ContextBudget = contextBudget,

            // The preset itself is stored as "no choice" so a future preset update applies.
            DefaultExtensions = extensions.SequenceEqual(ExtensionRules.NormalizeAll(ExtensionRules.ConfigDocsPreset), StringComparer.Ordinal) ? null : extensions,
            Theme = (ThemeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Dark",
            EditorFontSize = size,
            BackupRetention = retention,
            CloseToTray = CloseToTrayBox.IsChecked == true,
            AiApproval = Result.AiApproval with
            {
                Enabled = ApprovalEnabledBox.IsChecked == true,
                DetectClaude = DetectClaudeBox.IsChecked == true,
                DetectCodex = DetectCodexBox.IsChecked == true,
                DetectAgy = DetectAgyBox.IsChecked == true,
                ScreenDetection = ScreenDetectionBox.IsChecked == true,
                PinWaitingToTop = PinWaitingBox.IsChecked == true,
                NotificationCards = CardsBox.IsChecked == true,
                NotificationSound = CardSoundBox.IsChecked == true,
            },
            SessionHooks = _sessionHooks.Result(),
        };
        DialogResult = true;
    }

    /// <summary>"AI 컨텍스트" tab values, or null after showing what is wrong.</summary>
    private ContextBudgetSettings? ParseContextBudget()
    {
        static int? Number(string text, int min, int max) =>
            int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max ? value : null;

        if (Number(ContextInstructionBox.Text, 100, 100_000) is not { } instruction)
        {
            ShowError("지침 파일 권장 크기는 100~100000 사이 정수여야 합니다.");
            return null;
        }

        if (Number(ContextShareBox.Text, 1, 100) is not { } share)
        {
            ShowError("MCP·기타 경고 비율은 1~100 사이 정수여야 합니다.");
            return null;
        }

        if (Number(ContextDescriptionBox.Text, 10, 10_000) is not { } description)
        {
            ShowError("긴 설명 기준은 10~10000 사이 정수여야 합니다.");
            return null;
        }

        if (Number(ContextTotalBox.Text, 1000, 1_000_000) is not { } total)
        {
            ShowError("기본 소모 경고는 1000~1000000 사이 정수여야 합니다.");
            return null;
        }

        if (Number(ContextWarningPercentBox.Text, 1, 100) is not { } warning
            || Number(ContextDangerPercentBox.Text, 1, 100) is not { } danger || danger < warning)
        {
            ShowError("세션 목록 사용률 기준은 1~100 사이 정수이고, 빨강이 주황 이상이어야 합니다.");
            return null;
        }

        var windows = new List<ModelWindowRule>();
        var lineNumber = 0;
        foreach (var raw in ContextWindowsBox.Text.Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var equals = line.LastIndexOf('=');
            if (equals <= 0 || Number(line[(equals + 1)..], 1000, 100_000_000) is not { } tokens)
            {
                ShowError($"모델별 창 크기 {lineNumber}번째 줄은 \"문자열=토큰\" 형식이어야 합니다 (토큰 1000~100000000).");
                return null;
            }

            windows.Add(new ModelWindowRule(line[..equals].Trim(), tokens));
        }

        return Result.ContextBudget with
        {
            InstructionFileTokens = instruction,
            McpSharePercent = share,
            DescriptionTokens = description,
            TotalWarningTokens = total,
            UsageWarningPercent = warning,
            UsageDangerPercent = danger,
            ClaudeContextWindows = windows,
            McpDirectMeasure = McpDirectMeasureBox.IsChecked == true,
        };
    }

    private static string FormatWindows(IEnumerable<ModelWindowRule> windows) =>
        string.Join(Environment.NewLine, windows.Select(w => w.Match + "=" + w.Tokens.ToString(CultureInfo.InvariantCulture)));

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
