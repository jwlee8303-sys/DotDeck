using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using DotDeck.Core.AiCli.Launch;
using DotDeck.Core.AiCli.Scope;
using DotDeck.Core.AiCli.Sessions;
using DotDeck.Core.Files;
using DotDeck.Core.Paths;
using DotDeck.Core.Projects;

namespace DotDeck.App.ViewModels.Dialogs;

/// <summary>A project row of the session dialog (SPEC 11.2 rules decide what can be picked).</summary>
public sealed partial class SessionProjectChoice : ObservableObject
{
    public SessionProjectChoice(ProjectDefinition project, ScopeEligibility eligibility)
    {
        Id = project.Id;
        Name = project.Name;
        Root = eligibility.Root ?? string.Empty;
        Warning = eligibility.Reason;
        IsSelectable = eligibility.IsSelectable;
        IsGlobal = eligibility.Status == ScopeEligibilityStatus.EligibleGlobalSettings;
    }

    public string Id { get; }

    public string Name { get; }

    public string Root { get; }

    /// <summary>"전역 설정이 수정될 수 있음", "폴더 없음", …</summary>
    public string? Warning { get; }

    public bool IsSelectable { get; }

    public bool IsGlobal { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// "Claude 세션 시작" dialog (SPEC 11.3-3): primary project, additional projects, CLI, mode, prompt, options and the
/// read-only command-line preview. Claude starts as an interactive terminal (default) or a command run; Codex: 5c.
/// </summary>
public sealed partial class SessionLaunchViewModel : ObservableObject
{
    private readonly Func<AiSessionStartRequest, string> _preview;
    private readonly Func<string, IReadOnlyList<string>> _lastAdditional;

    public SessionLaunchViewModel(
        IReadOnlyList<ProjectDefinition> projects,
        AppPaths paths,
        IFileStore store,
        string? primaryProjectId,
        Func<string, IReadOnlyList<string>> lastAdditional,
        Func<AiSessionStartRequest, string> preview,
        bool canSavePresets = true)
    {
        ArgumentNullException.ThrowIfNull(projects);
        _preview = preview ?? throw new ArgumentNullException(nameof(preview));
        _lastAdditional = lastAdditional ?? throw new ArgumentNullException(nameof(lastAdditional));
        CanSavePresets = canSavePresets;

        var rows = projects
            .Where(p => !p.Hidden)
            .Select(p => (Project: p, Eligibility: ScopeEligibility.Evaluate(p, paths, store)))
            .Where(r => r.Eligibility.Status != ScopeEligibilityStatus.NotSelectable)
            .ToList();

        // Disabled projects are listed with their reason but cannot be picked (SPEC 11.2).
        PrimaryChoices = [.. rows.Select(r => new SessionProjectChoice(r.Project, r.Eligibility))];
        _allRows = rows;

        // A project the caller names explicitly (e.g. "이 프로젝트에서 Claude 시작") wins, global ones included;
        // otherwise a global project is never picked by default (SPEC 11.2).
        _primary = PrimaryChoices.FirstOrDefault(c => c.Id == primaryProjectId && c.IsSelectable)
            ?? PrimaryChoices.FirstOrDefault(c => c.IsSelectable && !c.IsGlobal);
        RebuildAdditional();
    }

    /// <summary>The id of the project whose root holds <paramref name="path"/> (the deepest root wins), or null.</summary>
    public static string? OwnerOf(string path, IEnumerable<(string Id, string? Root)> projects)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(projects);
        return projects
            .Where(p => p.Root is { Length: > 0 } root && path.StartsWith(WithSeparator(root), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Root!.Length)
            .Select(p => p.Id)
            .FirstOrDefault();

        // "D:\" keeps its separator when trimmed; "C:\work\" and "C:\work" both become "C:\work\".
        static string WithSeparator(string root) => Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
    }

    /// <summary>False when sessions.json cannot be written (newer version or unreadable): presets cannot be saved.</summary>
    public bool CanSavePresets { get; }

    private readonly List<(ProjectDefinition Project, ScopeEligibility Eligibility)> _allRows;

    public IReadOnlyList<SessionProjectChoice> PrimaryChoices { get; }

    public ObservableCollection<SessionProjectChoice> AdditionalChoices { get; } = [];

    [ObservableProperty]
    private SessionProjectChoice? _primary;

    [ObservableProperty]
    private string _prompt = string.Empty;

    /// <summary>Interactive terminal (default) or a command run with a prompt (SPEC 11.5).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCommandRun))]
    private bool _isInteractive = true;

    public bool IsCommandRun
    {
        get => !IsInteractive;
        set => IsInteractive = !value;
    }

    [ObservableProperty]
    private bool _includeOpenDocuments = true;

    [ObservableProperty]
    private bool _resumeConversation;

    [ObservableProperty]
    private bool _allowReadOnlyShell;

    [ObservableProperty]
    private bool _saveAsPreset;

    [ObservableProperty]
    private string _presetName = string.Empty;

    [ObservableProperty]
    private string _commandLinePreview = string.Empty;

    [ObservableProperty]
    private string? _error;

    /// <summary>Shown next to the disabled choices.</summary>
    public string CodexNote => "Codex는 다음 단계(5c)에서 지원합니다.";

    public bool HasSelectableProject => PrimaryChoices.Any(c => c.IsSelectable);

    public bool IncludesGlobalProject =>
        (Primary?.IsGlobal ?? false) || AdditionalChoices.Any(c => c.IsSelected && c.IsGlobal);

    partial void OnPrimaryChanged(SessionProjectChoice? value) => RebuildAdditional();

    partial void OnPromptChanged(string value) => RefreshPreview();

    partial void OnIsInteractiveChanged(bool value) => RefreshPreview();

    partial void OnIncludeOpenDocumentsChanged(bool value) => RefreshPreview();

    partial void OnResumeConversationChanged(bool value) => RefreshPreview();

    partial void OnAllowReadOnlyShellChanged(bool value) => RefreshPreview();

    /// <summary>The request for the current choices, or null with <see cref="Error"/> set.</summary>
    public AiSessionStartRequest? TryBuildRequest()
    {
        Error = Validate();
        return Error is null ? BuildRequest() : null;
    }

    private string? Validate()
    {
        if (Primary is not { IsSelectable: true })
        {
            return "주 프로젝트를 선택하세요.";
        }

        if (!IsInteractive && string.IsNullOrWhiteSpace(Prompt))
        {
            return "실행할 프롬프트를 입력하세요.";
        }

        return SaveAsPreset && CanSavePresets && string.IsNullOrWhiteSpace(PresetName) ? "프리셋 이름을 입력하세요." : null;
    }

    private AiSessionStartRequest BuildRequest() => new()
    {
        PrimaryProjectId = Primary?.Id ?? string.Empty,
        AdditionalProjectIds = [.. AdditionalChoices.Where(c => c.IsSelected).Select(c => c.Id)],
        Cli = CliKind.Claude,
        Mode = IsInteractive ? LaunchMode.Interactive : LaunchMode.NonInteractive,
        Prompt = IsInteractive ? null : Prompt.Trim(),
        IncludeOpenDocuments = IncludeOpenDocuments,
        ResumeConversation = ResumeConversation,
        AllowReadOnlyShell = !IsInteractive && AllowReadOnlyShell,
    };

    private void RebuildAdditional()
    {
        foreach (var choice in AdditionalChoices)
        {
            choice.PropertyChanged -= OnAdditionalChanged;
        }

        AdditionalChoices.Clear();
        if (Primary is { } primary)
        {
            var remembered = _lastAdditional(primary.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var (project, eligibility) in _allRows.Where(r => r.Project.Id != primary.Id))
            {
                // Remembered choices win; global projects are never picked by default (SPEC 11.2).
                var choice = new SessionProjectChoice(project, eligibility)
                {
                    IsSelected = eligibility.IsSelectable && remembered.Contains(project.Id),
                };
                choice.PropertyChanged += OnAdditionalChanged;
                AdditionalChoices.Add(choice);
            }
        }

        RefreshPreview();
    }

    private void OnAdditionalChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionProjectChoice.IsSelected))
        {
            RefreshPreview();
        }
    }

    private void RefreshPreview()
    {
        var request = BuildRequest();
        if (!IsInteractive && string.IsNullOrWhiteSpace(Prompt))
        {
            request = request with { Prompt = "(프롬프트)" };
        }

        CommandLinePreview = Primary is null ? string.Empty : _preview(request);
        OnPropertyChanged(nameof(IncludesGlobalProject));
    }
}
