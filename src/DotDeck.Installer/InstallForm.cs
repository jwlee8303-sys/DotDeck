using System.Diagnostics;
using System.Drawing;

namespace DotDeck.Installer;

/// <summary>Folder choice ([찾아보기]/[설치]/[취소]) before the embedded Velopack setup installs into it.</summary>
internal sealed class InstallForm : Form
{
    private static readonly Color Back = Color.FromArgb(0x1F, 0x1F, 0x1F);
    private static readonly Color Field = Color.FromArgb(0x33, 0x33, 0x37);
    private static readonly Color Fore = Color.FromArgb(0xF1, 0xF1, 0xF1);
    private static readonly Color Muted = Color.FromArgb(0xB0, 0xB0, 0xB0);
    private static readonly Color Accent = Color.FromArgb(0x00, 0x7A, 0xCC);

    private readonly string _packId;
    private readonly TextBox _folder;
    private readonly Button _install;
    private readonly Button _cancel;
    private readonly Button _browse;

    public InstallForm(string packId, string defaultFolder)
    {
        _packId = packId;
        Text = packId + " 설치";
        Font = new Font("Segoe UI", 9.75f);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Back;
        ForeColor = Fore;
        ClientSize = new Size(560, 190);

        var title = new Label { Text = packId + "을(를) 설치할 폴더를 선택하세요.", AutoSize = true, Location = new Point(20, 20) };
        var hint = new Label
        {
            Text = "현재 사용자에게만 설치되며 관리자 권한이 필요하지 않습니다. 업데이트도 이 폴더에 적용됩니다.",
            AutoSize = true,
            ForeColor = Muted,
            Location = new Point(20, 46),
        };
        _folder = new TextBox
        {
            Text = defaultFolder,
            Location = new Point(20, 84),
            Width = 420,
            BackColor = Field,
            ForeColor = Fore,
            BorderStyle = BorderStyle.FixedSingle,
            AccessibleName = "설치 폴더",
            AccessibleRole = AccessibleRole.Text,
        };
        _browse = MakeButton("찾아보기...", new Point(450, 82), false);
        _install = MakeButton("설치", new Point(350, 140), true);
        _cancel = MakeButton("취소", new Point(450, 140), false);

        _browse.Click += (_, _) => Browse();
        _install.Click += async (_, _) => await InstallAsync();
        _cancel.Click += (_, _) => Close();
        AcceptButton = _install;
        CancelButton = _cancel;
        Controls.AddRange([title, hint, _folder, _browse, _install, _cancel]);
    }

    /// <summary>True once the setup finished successfully (it starts the app itself).</summary>
    public bool Installed { get; private set; }

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
            Description = "설치 폴더 선택 (" + _packId + " 폴더를 새로 만들려면 [새 폴더 만들기]를 사용하세요)",
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
        var folder = ResolveFolder(_folder.Text);
        if (folder is null) return;

        var createdFolder = !Directory.Exists(folder);
        if (!CanWrite(folder))
        {
            Error("이 폴더에 쓸 수 없습니다. 쓰기 권한이 있는 다른 폴더를 선택하세요.\n\n" + folder);
            return;
        }

        string? tempDirectory = null;
        try
        {
            tempDirectory = Path.Combine(Path.GetTempPath(), _packId + "-Setup-" + Guid.NewGuid().ToString("N"));
            var setup = ExtractSetup(tempDirectory);
            if (setup is null)
            {
                Error("설치 데이터가 포함되지 않은 설치 파일입니다. 배포 페이지에서 설치 파일을 다시 내려받으세요.");
                return;
            }

            SetBusy(true);
            Hide();
            var exitCode = await Task.Run(() => RunSetup(setup, folder)).ConfigureAwait(true);
            if (exitCode == 0)
            {
                Installed = true;
                Close();
                return;
            }

            Show();
            Error("설치를 완료하지 못했습니다(종료 코드 " + exitCode + "). 잠시 후 다시 시도하세요.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Show();
            Error("설치를 시작하지 못했습니다.\n\n" + ex.Message);
        }
        finally
        {
            SetBusy(false);
            TryDelete(tempDirectory);
            if (!Installed && createdFolder) TryRemoveEmpty(folder);
        }
    }

    /// <summary>A full local or UNC path that is not a drive root; a non-empty folder that is not an install gets a subfolder.</summary>
    private string? ResolveFolder(string input)
    {
        var text = input.Trim().Trim('"').TrimEnd('\\', '/');
        var rooted = (text.Length >= 3 && char.IsLetter(text[0]) && text[1] == ':' && (text[2] == '\\' || text[2] == '/'))
            || text.StartsWith(@"\\", StringComparison.Ordinal);
        string full;
        try
        {
            full = rooted ? Path.GetFullPath(text) : "";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            full = "";
        }

        if (full.Length == 0 || Path.GetPathRoot(full)?.TrimEnd('\\') == full.TrimEnd('\\'))
        {
            Error("설치 폴더의 전체 경로를 입력하세요(드라이브 루트 제외). 예: " + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), _packId));
            return null;
        }

        if (File.Exists(full))
        {
            Error("같은 이름의 파일이 있습니다. 다른 폴더를 선택하세요.\n\n" + full);
            return null;
        }

        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any() && !File.Exists(Path.Combine(full, "Update.exe")))
        {
            // Setup owns its folder; never install into one holding other files.
            var nested = Path.Combine(full, _packId);
            var answer = MessageBox.Show(this,
                "선택한 폴더가 비어 있지 않습니다. 하위 폴더에 설치할까요?\n\n" + nested,
                Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
            if (answer != DialogResult.OK) return null;
            _folder.Text = nested;
            return ResolveFolder(nested);
        }

        return full;
    }

    private static bool CanWrite(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
        {
            return false;
        }
    }

    private static string? ExtractSetup(string directory)
    {
        using var resource = typeof(InstallForm).Assembly.GetManifestResourceStream("Setup.exe");
        if (resource is null) return null;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Setup.exe");
        using (var file = File.Create(path))
        {
            resource.CopyTo(file);
        }

        return path;
    }

    private static int RunSetup(string setup, string folder)
    {
        // One argument: the absolute folder, quoted for spaces (trailing separators were trimmed, so no \" escape).
        var start = new ProcessStartInfo(setup, "--installto \"" + folder + "\"") { UseShellExecute = false };
        using var process = Process.Start(start) ?? throw new IOException("설치 프로그램을 시작하지 못했습니다.");
        process.WaitForExit();
        return process.ExitCode;
    }

    private void SetBusy(bool busy)
    {
        _install.Enabled = _cancel.Enabled = _browse.Enabled = _folder.Enabled = !busy;
        UseWaitCursor = busy;
    }

    private void Error(string message) => MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private static void TryDelete(string? directory)
    {
        if (directory is null) return;
        try { Directory.Delete(directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine(ex.Message); }
    }

    private static void TryRemoveEmpty(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine(ex.Message); }
    }
}
