using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace DotDeck.Installer;

/// <summary>Result of a setup step: an exit code and, on failure, a message for the user.</summary>
internal sealed class SetupResult(int exitCode, string? message = null)
{
    public int ExitCode { get; } = exitCode;
    public string? Message { get; } = message;
    public bool Succeeded => ExitCode is ExitCodes.Success or ExitCodes.RebootRequired;
}

/// <summary>
/// Install flow shared by the window and <c>--silent</c>, running with the user's normal rights:
/// validate the folder → one elevated step (UAC) for the machine-wide MSI → per-user steps (autostart, cleanup of a
/// per-user 1.5 install's entries) → start DotDeck with normal rights.
/// </summary>
internal sealed class SetupEngine(string packId, string version)
{
    private const string PreferenceKey = @"Software\DotDeck\Setup";

    public string PackId { get; } = packId;
    public string Version { get; } = version;
    public InstallState State { get; } = InstallState.Read(packId);
    public string LogPath { get; set; } = Path.Combine(Path.GetTempPath(), "DotDeck-Setup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");

    public string DefaultFolder => State.MachineFolder ?? Paths.DefaultMachineFolder(PackId);

    /// <summary>Reinstall/upgrade keeps the current choice; a first install remembers an earlier one, else on.</summary>
    public bool DefaultAutoStart
    {
        get
        {
            if (State.MachineFolder is not null) return Shortcuts.AutoStartEnabled;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PreferenceKey);
                if (key?.GetValue("AutoStart") is int stored) return stored != 0;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                Debug.WriteLine(ex.Message);
            }

            return true;
        }
    }

    /// <summary>"DOMAIN\user" of the account the autostart shortcut applies to.</summary>
    public static string CurrentAccount => Environment.UserDomainName + "\\" + Environment.UserName;

    /// <summary>
    /// Checks the folder. <paramref name="useSubfolder"/> is asked when a non-empty unrelated folder was chosen
    /// (true: install into &lt;folder&gt;\DotDeck). Returns the folder, or null with <paramref name="error"/>.
    /// </summary>
    public string? ResolveFolder(string input, Func<string, bool> useSubfolder, out string? error)
    {
        error = null;
        var text = input.Trim().Trim('"').TrimEnd('\\', '/');
        string full;
        try
        {
            full = Paths.IsLocalFullPath(text) ? Paths.Normalize(text) : "";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            full = "";
        }

        if (full.Length == 0 || Path.GetPathRoot(full)?.TrimEnd('\\') == full)
        {
            error = "설치 폴더의 전체 경로를 입력하세요(드라이브 루트·네트워크 경로 제외). 예: " + Paths.DefaultMachineFolder(PackId);
            return null;
        }

        if (Paths.DataFolders(PackId).Any(data => Paths.IsInside(full, data) || Paths.IsInside(data, full)))
        {
            error = "DotDeck 설정·데이터 폴더(또는 그 상위 폴더)에는 설치할 수 없습니다. 제거할 때 설치 폴더 전체가 삭제됩니다.\n\n" + full;
            return null;
        }

        if (File.Exists(full))
        {
            error = "같은 이름의 파일이 있습니다. 다른 폴더를 선택하세요.\n\n" + full;
            return null;
        }

        var ownFolder = (State.MachineFolder is not null && Paths.SameFolder(full, State.MachineFolder))
            || (State.LegacyRoot is not null && Paths.SameFolder(full, State.LegacyRoot));
        if (!ownFolder && Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
        {
            // Uninstall removes the whole install folder: never install into one holding other files.
            var nested = Path.Combine(full, PackId);
            return useSubfolder(nested) ? ResolveFolder(nested, _ => false, out error) : null;
        }

        return full;
    }

    /// <summary>Problems that need the user before the UAC prompt (newer version installed, app running).</summary>
    public string? CheckBeforeInstall(string folder, out int exitCode)
    {
        exitCode = ExitCodes.Success;
        if (InstallState.IsNewer(State.MachineVersion, Version))
        {
            exitCode = ExitCodes.NewerVersionInstalled;
            return $"더 새 버전({State.MachineVersion})이 이미 설치되어 있습니다. 이 설치 파일({Version})로는 설치하지 않습니다.";
        }

        if (State.MachineFolder is not null && State.MachineVersion == Version && !Paths.SameFolder(folder, State.MachineFolder))
        {
            exitCode = ExitCodes.InvalidArguments;
            return $"같은 버전이 이미 다음 폴더에 설치되어 있습니다. 다른 폴더로 옮기려면 먼저 Windows 설정 > 앱에서 DotDeck을 제거하세요.\n\n{State.MachineFolder}";
        }

        var running = RunningApp(folder);
        if (running is not null)
        {
            exitCode = ExitCodes.AppRunning;
            return "DotDeck이 실행 중입니다. 작업을 저장하고 DotDeck을 종료한 뒤 다시 시도하세요.\n\n" + running;
        }

        return null;
    }

    /// <summary>
    /// Runs the elevated step. UAC refused → <see cref="ExitCodes.Cancelled"/> with nothing changed. On success
    /// applies the per-user steps and (unless <paramref name="launch"/> is false) starts DotDeck with normal rights.
    /// </summary>
    public SetupResult Install(string folder, bool autoStart, bool launch, bool quiet)
    {
        Log($"설치 시작: {Version} → {folder} (자동 실행 {(autoStart ? "켬" : "끔")}, 기존 per-user {State.LegacyRoot ?? "없음"})");
        var status = Path.Combine(Path.GetTempPath(), "DotDeck-Setup-" + Guid.NewGuid().ToString("N") + ".status");
        var msiLog = Path.ChangeExtension(LogPath, ".msi.log");
        var args = new StringBuilder("--elevated-install");
        args.Append(" --dir ").Append(ElevatedInstall.Quote(folder));
        args.Append(" --msi-log ").Append(ElevatedInstall.Quote(msiLog));
        args.Append(" --status ").Append(ElevatedInstall.Quote(status));
        if (State.LegacyRoot is not null) args.Append(" --legacy-root ").Append(ElevatedInstall.Quote(State.LegacyRoot));
        if (quiet) args.Append(" --quiet");

        int code;
        try
        {
            var start = new ProcessStartInfo(Application.ExecutablePath, args.ToString()) { UseShellExecute = true, Verb = "runas" };
            using var process = Process.Start(start) ?? throw new IOException("설치 프로그램을 시작하지 못했습니다.");
            process.WaitForExit();
            code = process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log("관리자 권한 요청이 취소되어 설치하지 않았습니다.");
            return new SetupResult(ExitCodes.Cancelled, "관리자 권한 요청이 취소되어 설치하지 않았습니다. 변경된 내용은 없습니다.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log("관리자 권한으로 시작하지 못했습니다: " + ex.Message);
            return new SetupResult(ExitCodes.Failed, "관리자 권한으로 설치를 시작하지 못했습니다.\n\n" + ex.Message);
        }

        var detail = ReadStatus(status);
        Log($"관리자 단계 종료 코드 {code}{(detail is null ? "" : ": " + detail)} (MSI 로그 {msiLog})");
        if (code is not (ExitCodes.Success or ExitCodes.RebootRequired))
        {
            var message = code == ExitCodes.Cancelled
                ? "설치를 취소했습니다. 변경된 내용은 없습니다."
                : $"설치를 완료하지 못했습니다(코드 {code}).\n{detail}\n\n로그: {msiLog}";
            return new SetupResult(code, message);
        }

        var warnings = new List<string>();
        Try(warnings, "기존 사용자용 설치 항목 정리", () =>
        {
            if (State.LegacyRoot is null) return;
            InstallState.RemoveLegacyEntry(PackId, State.LegacyRoot);
            Shortcuts.RemoveUserShortcutsInto(State.LegacyRoot);
        });
        Try(warnings, "자동 실행 설정", () =>
        {
            Shortcuts.SetAutoStart(autoStart, folder);
            using var key = Registry.CurrentUser.CreateSubKey(PreferenceKey);
            key.SetValue("AutoStart", autoStart ? 1 : 0, RegistryValueKind.DWord);
        });
        if (launch) Try(warnings, "DotDeck 실행", () => LaunchAsUser(folder));

        Log("설치 완료" + (warnings.Count == 0 ? "" : " (경고: " + string.Join("; ", warnings) + ")"));
        var note = code == ExitCodes.RebootRequired ? "설치를 마치려면 Windows를 다시 시작하세요." : null;
        if (warnings.Count > 0) note = (note is null ? "" : note + "\n\n") + "설치는 완료했지만 일부 작업을 하지 못했습니다:\n" + string.Join("\n", warnings);
        return new SetupResult(code, note);
    }

    /// <summary>
    /// Starts the install folder's launcher with normal rights. Setup itself runs as the invoking user (asInvoker);
    /// if it was started elevated anyway, Explorer starts the app so it does not inherit the admin token.
    /// </summary>
    private static void LaunchAsUser(string folder)
    {
        var exe = Path.Combine(folder, "DotDeck.exe");
        if (IsElevated())
        {
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), ElevatedInstall.Quote(exe)) { UseShellExecute = false })?.Dispose();
        }
        else
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = folder })?.Dispose();
        }
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private string? RunningApp(string folder)
    {
        var roots = new[] { folder, State.MachineFolder, State.LegacyRoot }.Where(r => r is not null).Cast<string>().ToArray();
        foreach (var process in Process.GetProcessesByName("DotDeck"))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is not null && roots.Any(root => Paths.IsInside(path, root))) return path;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    Debug.WriteLine(ex.Message); // Exited, or another user's process: not ours to close.
                }
            }
        }

        return null;
    }

    private void Try(List<string> warnings, string step, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                                       or System.Runtime.InteropServices.COMException or System.Reflection.TargetInvocationException
                                       or System.ComponentModel.Win32Exception)
        {
            Log(step + " 실패: " + ex.Message);
            warnings.Add(step + ": " + ex.Message);
        }
    }

    private static string? ReadStatus(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path, Encoding.UTF8).Trim();
            File.Delete(path);
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    public void Log(string message)
    {
        try { File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + message + Environment.NewLine, Encoding.UTF8); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine(ex.Message); }
    }
}
