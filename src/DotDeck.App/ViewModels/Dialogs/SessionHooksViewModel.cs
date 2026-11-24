using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotDeck.App.Services;
using DotDeck.App.ViewModels.Editor;
using DotDeck.Core.AiCli.Approvals;
using DotDeck.Core.AiCli.Guard;
using DotDeck.Core.AiCli.Launch;
using DotDeck.Core.AiCli.Scope;
using DotDeck.Core.Editors;
using DotDeck.Core.Hooks;
using DotDeck.Core.Security;

namespace DotDeck.App.ViewModels.Dialogs;

/// <summary>One of DotDeck's own hooks, shown read-only.</summary>
public sealed record DefaultHookRow(string Name, string Event, string Matcher, string Command, string Timeout, string Note);

/// <summary>A hook of the user's global Claude settings (reference only, command masked).</summary>
public sealed record GlobalHookRow(string Event, string Matcher, string Command);

/// <summary>
/// "AI 세션 훅" (환경 설정): DotDeck's guard and approval hooks on/off, the user's extra hooks for DotDeck Claude
/// sessions, a preview of the settings.json a new session gets, and the global hooks that also run. The user's global
/// Claude settings are only read.
/// </summary>
public sealed partial class SessionHooksViewModel : ObservableObject
{
    private const string SessionIdPlaceholder = "{세션 ID}";
    private readonly string _guardExecutable;
    private readonly string _sessionDirectory;
    private readonly IDialogService? _dialogs;
    private readonly string _workingDirectory;
    private ClaudeSettingsDocument _userDocument;

    /// <param name="current">Saved settings.</param>
    /// <param name="guardExecutable">DotDeck.exe, as written into the hooks.</param>
    /// <param name="sessionsDirectory">Parent of the session folders (preview only).</param>
    /// <param name="claudeHome">The user's <c>.claude</c> folder (global hooks, read only).</param>
    /// <param name="dialogs">Confirms hook test runs.</param>
    /// <param name="workingDirectory">Hook test working folder.</param>
    public SessionHooksViewModel(
        SessionHookSettings current,
        string guardExecutable,
        string sessionsDirectory,
        string claudeHome,
        IDialogService? dialogs,
        string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(current);
        _guardExecutable = guardExecutable;
        _sessionDirectory = Path.Combine(sessionsDirectory, SessionIdPlaceholder);
        _dialogs = dialogs;
        _workingDirectory = workingDirectory;
        _guard = current.Guard;
        _approvalHooks = current.Approval;
        _userDocument = DocumentOf(current.UserHooks);
        HookTable = new HookTableViewModel
        {
            GetEvents = () => HookEventCatalog.BuiltIn,
            Changed = OnUserHooksChanged,
            RunHook = RunHook,
        };
        HookTable.Load(new ClaudeSettingsHookStore(_userDocument));
        GlobalSettingsPath = Path.Combine(claudeHome, "settings.json");
        GlobalHooks = ReadGlobalHooks(GlobalSettingsPath, out var globalNote);
        GlobalHooksNote = globalNote;
        Refresh();
    }

    public HookTableViewModel HookTable { get; }

    /// <summary>Runs a hook row in a hook test window (set by the dialog).</summary>
    public Action<Tools.HookTestPaneViewModel>? ShowHookTest { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GuardOffNote))]
    private bool _guard;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApprovalOffNote))]
    private bool _approvalHooks;

    [ObservableProperty]
    private IReadOnlyList<DefaultHookRow> _defaultHooks = [];

    [ObservableProperty]
    private string _preview = string.Empty;

    [ObservableProperty]
    private string? _userHooksWarning;

    public string GuardOffNote => Guard
        ? "도구 실행 전에 프로젝트 범위 밖 쓰기·비밀 파일·위험 명령을 검사해 막거나 확인을 요청합니다."
        : "끄면 DotDeck이 도구 실행을 검사하지 않습니다(설정의 permissions 차단은 유지). 새 세션의 AI 모니터 행에 \"가드 꺼짐\"이 표시됩니다.";

    public string ApprovalOffNote => ApprovalHooks
        ? "대화형 Claude 세션의 승인 요청을 DotDeck으로 받아 AI 모니터와 알림 카드에서 허용·거부할 수 있게 합니다."
        : "끄면 이 세션의 승인 요청은 화면 감지로만 추정합니다(확인한 CLI 버전이고 탭이 보일 때만 키로 응답).";

    public string GlobalSettingsPath { get; }

    public IReadOnlyList<GlobalHookRow> GlobalHooks { get; }

    public bool HasGlobalHooks => GlobalHooks.Count > 0;

    public string GlobalHooksNote { get; }

    /// <summary>The settings to save; extra hooks in Claude's <c>hooks</c> format.</summary>
    public SessionHookSettings Result()
    {
        HookTable.Write();
        return new SessionHookSettings
        {
            Guard = Guard,
            Approval = ApprovalHooks,
            UserHooks = UserHooksJson(),
        }.Normalize();
    }

    [RelayCommand]
    private void ResetToDefaults()
    {
        if (_dialogs is { } dialogs && HookTable.Rows.Count > 0
            && !dialogs.Confirm("AI 세션 훅", $"DotDeck 기본 훅을 모두 켜고 사용자 추가 훅 {HookTable.Rows.Count}개를 지웁니다.\n\n계속할까요?"))
        {
            return;
        }

        Guard = true;
        ApprovalHooks = true;
        _userDocument = DocumentOf(null);
        HookTable.Load(new ClaudeSettingsHookStore(_userDocument));
        Refresh();
    }

    partial void OnGuardChanged(bool value) => Refresh();

    partial void OnApprovalHooksChanged(bool value) => Refresh();

    private void OnUserHooksChanged()
    {
        HookTable.Write();
        Refresh();
    }

    private void RunHook(HookRow row)
    {
        var test = new Tools.HookTestPaneViewModel { Dialogs = _dialogs, WorkingDirectory = _workingDirectory };
        test.Load(row.Event, row.Matcher, row.Command);
        ShowHookTest?.Invoke(test);
    }

    private JsonObject? UserHooksJson() =>
        JsonNode.Parse(_userDocument.ToJson()) is JsonObject root && root["hooks"] is JsonObject hooks && hooks.Count > 0
            ? (JsonObject)hooks.DeepClone()
            : null;

    private void Refresh()
    {
        DefaultHooks =
        [
            new("세션 시작 확인 (필수)", "SessionStart", string.Empty, Show(GuardHost.HeartbeatSwitch, _sessionDirectory), "10",
                "세션 설정이 적용됐는지 확인합니다. 끌 수 없습니다."),
            new("도구 실행 가드", "PreToolUse", "*", Show(GuardHost.GuardSwitch, Path.Combine(_sessionDirectory, SessionFileNames.Scope)), "10 (실패 시 차단)",
                Guard ? "켜짐" : "꺼짐"),
            new("승인 요청", "PermissionRequest", "*", Show(ClaudeApprovalHook.RequestSwitch, _sessionDirectory),
                ClaudeApprovalHook.HookTimeoutSeconds.ToString(CultureInfo.InvariantCulture) + " (대기 상한 30분 + 30초)",
                ApprovalHooks ? "켜짐 · 대화형 세션만" : "꺼짐"),
            new("승인 실행 확인", "PostToolUse", "*", Show(ClaudeApprovalHook.ToolRanSwitch, _sessionDirectory),
                ClaudeApprovalHook.ToolRanTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
                ApprovalHooks ? "켜짐 · 승인 대기 중일 때만 DotDeck에 연결" : "꺼짐"),
        ];

        var userHooks = UserHooksJson();
        UserHooksWarning = userHooks?["PermissionRequest"] is JsonArray { Count: > 0 }
            ? "PermissionRequest 사용자 훅이 allow/deny를 반환하면 DotDeck 승인 화면과 별개로 승인 결과에 영향을 줄 수 있습니다. 여러 훅의 결정을 Claude가 어떻게 합치는지는 확인하지 않았습니다."
            : null;
        try
        {
            var scope = new ScopeDocument { SessionId = SessionIdPlaceholder };
            Preview = SessionFilesWriter.BuildSettings(
                _guardExecutable,
                _sessionDirectory,
                scope,
                new SessionHookPlan(Guard, ApprovalHooks, userHooks));
        }
        catch (ArgumentException ex)
        {
            Preview = "미리보기를 만들 수 없습니다: " + ex.Message;
        }
    }

    private string Show(string switchName, string argument) =>
        $"\"{_guardExecutable}\" {switchName} \"{argument}\"";

    private static ClaudeSettingsDocument DocumentOf(JsonObject? hooks)
    {
        var text = hooks is { Count: > 0 } ? new JsonObject { ["hooks"] = hooks.DeepClone() }.ToJsonString() : "{}";
        return ClaudeSettingsDocument.TryParse(text, out _) ?? ClaudeSettingsDocument.TryParse("{}", out _)!;
    }

    private static IReadOnlyList<GlobalHookRow> ReadGlobalHooks(string path, out string note)
    {
        try
        {
            if (!File.Exists(path))
            {
                note = "사용자 전역 settings.json이 없습니다.";
                return [];
            }

            if (ClaudeSettingsDocument.TryParse(File.ReadAllText(path), out var error) is not { } document)
            {
                note = "사용자 전역 settings.json을 읽지 못했습니다: " + error;
                return [];
            }

            var hooks = document.GetHooks()
                .Select(h => new GlobalHookRow(h.Event, h.Matcher.Length == 0 ? "(전체)" : h.Matcher, SecretMasker.MaskText(Describe(h))))
                .ToList();
            note = hooks.Count == 0
                ? "사용자 전역 settings.json에 훅이 없습니다."
                : $"아래 전역 훅 {hooks.Count}개도 DotDeck 세션에서 함께 실행됩니다(DotDeck은 이 파일을 고치지 않습니다).";
            return hooks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            note = "사용자 전역 settings.json을 읽지 못했습니다: " + ex.Message;
            return [];
        }
    }

    private static string Describe(HookHandlerModel hook) => hook.Type != "command"
        ? $"({hook.Type})"
        : hook.Args is { Count: > 0 } args ? hook.Command + " " + string.Join(' ', args) : hook.Command;
}
