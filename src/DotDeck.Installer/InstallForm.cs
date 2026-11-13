using System.Drawing;

namespace DotDeck.Installer;

/// <summary>
/// Install folder ([찾아보기]), "run at sign-in" choice and [설치]/[취소]. [설치] asks Windows for administrator rights
/// (UAC) for the machine-wide install; refusing it closes setup without changing anything.
/// </summary>
internal sealed class InstallForm : Form
{
    private static readonly Color Back = Color.FromArgb(0x1F, 0x1F, 0x1F);
    private static readonly Color Field = Color.FromArgb(0x33, 0x33, 0x37);
    private static readonly Color Fore = Color.FromArgb(0xF1, 0xF1, 0xF1);
    private static readonly Color Muted = Color.FromArgb(0xB0, 0xB0, 0xB0);
    private static readonly Color Accent = Color.FromArgb(0x00, 0x7A, 0xCC);

    private readonly SetupEngine _engine;
    private readonly TextBox _folder;
    private readonly CheckBox _autoStart;
    private readonly Button _install;
    private readonly Button _cancel;
    private readonly Button _browse;

    public InstallForm(SetupEngine engine)
    {
        _engine = engine;
        Text = engine.PackId + " " + engine.Version + " 설치";
        Font = new Font("Segoe UI", 9.75f);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Back;
        ForeColor = Fore;
        ClientSize = new Size(580, 268);

        var title = new Label { Text = engine.PackId + "을(를) 설치할 폴더를 선택하세요.", AutoSize = true, Location = new Point(20, 18) };
        var hint = new Label
        {
            Text = "이 PC의 모든 사용자용으로 설치합니다. [설치]를 누르면 Windows가 관리자 권한을 확인합니다.\n"
                + "업데이트도 이 폴더에 적용되며, 설정과 작업물(%APPDATA%·%LOCALAPPDATA%\\DotDeck)은 그대로 유지됩니다.",
            AutoSize = true,
            ForeColor = Muted,
            Location = new Point(20, 44),
        };
        _folder = new TextBox
        {
            Text = engine.DefaultFolder,
            Location = new Point(20, 96),
            Width = 440,
            BackColor = Field,
            ForeColor = Fore,
            BorderStyle = BorderStyle.FixedSingle,
            AccessibleName = "설치 폴더",
            AccessibleRole = AccessibleRole.Text,
        };
        _browse = MakeButton("찾아보기...", new Point(470, 94), false);
        _autoStart = new CheckBox
        {
            Text = "Windows 로그인 시 자동 실행",
            Checked = engine.DefaultAutoStart,
            AutoSize = true,
            Location = new Point(20, 140),
            FlatStyle = FlatStyle.System,
            AccessibleName = "Windows 로그인 시 자동 실행",
        };
        var scope = new Label
        {
            Text = "적용 범위: 현재 Windows 계정(" + SetupEngine.CurrentAccount + ")에만 적용됩니다. 다른 계정에는 등록되지 않습니다.",
            AutoSize = true,
            ForeColor = Muted,
            Location = new Point(38, 166),
            AccessibleName = "자동 실행 적용 범위",
        };
        var shortcuts = new Label
        {
            Text = "바탕화면과 시작 메뉴에 바로가기를 만듭니다. 제거: Windows 설정 > 앱 > 설치된 앱",
            AutoSize = true,
            ForeColor = Muted,
            Location = new Point(20, 194),
        };
        _install = MakeButton("설치", new Point(370, 224), true);
        _cancel = MakeButton("취소", new Point(470, 224), false);

        _browse.Click += (_, _) => Browse();
        _install.Click += async (_, _) => await InstallAsync();
        _cancel.Click += (_, _) => Close();
        AcceptButton = _install;
        CancelButton = _cancel;
        Controls.AddRange([title, hint, _folder, _browse, _autoStart, scope, shortcuts, _install, _cancel]);
    }

    /// <summary>Process exit code: 0 installed, 1602 cancelled (window closed or UAC refused), else the failure.</summary>
    public int ExitCode { get; private set; } = ExitCodes.Cancelled;

    private static Button MakeButton(string text, Point location, bool primary)
    {
        var button = new Button
        {
            Text = text,
            Location = location,
            Size = new Size(90, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Accent : Field,
            ForeColor = Fore,
            UseVisualStyleBackColor = false,
            AccessibleName = text,
            AccessibleRole = AccessibleRole.PushButton,
        };
        button.FlatAppearance.BorderColor = primary ? Accent : Color.FromArgb(0x55, 0x55, 0x55);
        return button;
    }

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "설치 폴더 선택 (" + _engine.PackId + " 폴더를 새로 만들려면 [새 폴더 만들기]를 사용하세요)",
            ShowNewFolderButton = true,
        };
        var current = _folder.Text.Trim();
        if (Directory.Exists(current)) dialog.SelectedPath = current;
        else if (Directory.Exists(Path.GetDirectoryName(current) ?? "")) dialog.SelectedPath = Path.GetDirectoryName(current);

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _folder.Text = dialog.SelectedPath;
        }
    }

    private async Task InstallAsync()
    {
        var folder = _engine.ResolveFolder(_folder.Text, AskSubfolder, out var error);
        if (folder is null)
        {
            if (error is not null) Warn(error);
            return;
        }

        _folder.Text = folder;
        var problem = _engine.CheckBeforeInstall(folder, out _);
        if (problem is not null)
        {
            Warn(problem);
            return;
        }

        var autoStart = _autoStart.Checked;
        SetBusy(true);
        Hide();
        var result = await Task.Run(() => _engine.Install(folder, autoStart, launch: true, quiet: false)).ConfigureAwait(true);
        SetBusy(false);
        ExitCode = result.ExitCode;
        if (result.Succeeded)
        {
            if (result.Message is not null) Info(result.Message);
            Close();
            return;
        }

        if (result.ExitCode == ExitCodes.Cancelled)
        {
            // UAC refused or the MSI cancelled: nothing was installed, setup ends here.
            Info(result.Message ?? "설치를 취소했습니다.");
            Close();
            return;
        }

        Show();
        Warn(result.Message ?? "설치를 완료하지 못했습니다.");
    }

    private bool AskSubfolder(string nested) =>
        MessageBox.Show(this, "선택한 폴더가 비어 있지 않습니다. 하위 폴더에 설치할까요?\n\n" + nested,
            Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1) == DialogResult.OK;

    private void SetBusy(bool busy)
    {
        _install.Enabled = _cancel.Enabled = _browse.Enabled = _folder.Enabled = _autoStart.Enabled = !busy;
        UseWaitCursor = busy;
    }

    private void Warn(string message) => MessageBox.Show(Visible ? this : null, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private void Info(string message) => MessageBox.Show(Visible ? this : null, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
}
