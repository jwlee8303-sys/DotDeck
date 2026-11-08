using System.IO;
using CommunityToolkit.Mvvm.Input;
using DotDeck.Core.Logging;
using DotDeck.App.ViewModels.Documents;
using DotDeck.Core.Explorer;
using DotDeck.Core.Projects;
using DotDeck.Core.SystemConfig;

namespace DotDeck.App.ViewModels;

/// <summary>System project (SPEC §8): environment tables, PATH editor, important files, elevation restart.</summary>
public sealed partial class ShellViewModel
{
    private SystemContext? _systemContext;

    /// <summary>Raised to close the window and start an elevated instance (the window saves session and layout).</summary>
    public event EventHandler? RestartElevatedRequested;

    private SystemContext SystemDocs => _systemContext ??= new SystemContext(
        _environment, _store, Paths, _dialogs, message => StatusMessage = message, RestartAsAdministrator, () => _backups.BackupsDirectory);

    /// <summary>Opens the document of a virtual System node (environment tables, PATH).</summary>
    public void OpenSystemNode(string nodeId)
    {
        var contentId = nodeId switch
        {
            SystemProjectSource.UserEnvironmentId => EnvironmentDocumentViewModel.IdFor(EnvironmentScope.User),
            SystemProjectSource.MachineEnvironmentId => EnvironmentDocumentViewModel.IdFor(EnvironmentScope.Machine),
            SystemProjectSource.PathId => PathDocumentViewModel.Id,
            _ => null,
        };
        if (contentId is null)
        {
            return;
        }

        ActiveContent = Documents.FirstOrDefault(d => d.ContentId == contentId) ?? CreateSystemDocument(contentId);
    }

    /// <summary>Creates (and adds) a system document for its ContentId; also used when a saved layout is restored.</summary>
    private DocumentViewModel? CreateSystemDocument(string contentId)
    {
        DocumentViewModel? document = contentId switch
        {
            "doc:env-user" => new EnvironmentDocumentViewModel(EnvironmentScope.User, SystemDocs, CloseDocument),
            "doc:env-machine" => new EnvironmentDocumentViewModel(EnvironmentScope.Machine, SystemDocs, CloseDocument),
            PathDocumentViewModel.Id => new PathDocumentViewModel(SystemDocs, CloseDocument),
            _ => null,
        };
        if (document is not null)
        {
            Documents.Add(document);
        }

        return document;
    }

    [RelayCommand]
    private void RestartAsAdministrator()
    {
        if (IsAdministrator)
        {
            _dialogs.ShowInfo("관리자 권한", "이미 관리자 권한으로 실행 중입니다.");
            return;
        }

        RestartElevatedRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Folder project selected in the project area (its own list), or null for the System project's list.</summary>
    private ProjectDefinition? SelectedCustomFilesOwner =>
        Projects.List.SelectedRow?.Explorer is { SupportsVisibility: true } explorer ? explorer.Project : null;

    /// <summary>Main menu: register by typing a path, into the selected project's "사용자 지정 파일" (else the System project's).</summary>
    [RelayCommand]
    private Task RegisterOtherFileAsync() => RegisterCustomFileAsync(SelectedCustomFilesOwner, browse: false);

    /// <summary>Main menu: add with the file picker.</summary>
    [RelayCommand]
    private Task AddImportantFileAsync() => RegisterCustomFileAsync(SelectedCustomFilesOwner, browse: true);

    /// <summary>
    /// Registers files in a "사용자 지정 파일" list (<paramref name="owner"/> null: the System project). Typing a path of
    /// a file that does not exist yet creates it empty (after confirmation) and opens it, so new configuration files
    /// can be started from here.
    /// </summary>
    private async Task RegisterCustomFileAsync(ProjectDefinition? owner, bool browse)
    {
        var title = owner is null ? "사용자 지정 파일 등록" : $"사용자 지정 파일 등록 — {owner.Name}";
        List<string> paths;
        if (browse)
        {
            var initial = owner is null ? Paths.Home : Projects.Explorers.FirstOrDefault(e => e.Id == owner.Id)?.Source.RootPath ?? Paths.Home;
            paths = [.. _dialogs.PickOpenFiles(title, initial)];
            if (paths.Count == 0)
            {
                return;
            }
        }
        else
        {
            var input = _dialogs.AskText(title, "등록할 파일의 전체 경로를 입력하세요. 없는 파일이면 새로 만듭니다.", string.Empty, ValidateOtherFilePath);
            if (input is null)
            {
                return;
            }

            var path = Path.GetFullPath(System.Environment.ExpandEnvironmentVariables(input.Trim().Trim('"')));
            if (!_store.FileExists(path))
            {
                if (!_dialogs.Confirm(title, $"파일이 없습니다. 빈 파일로 새로 만들까요?\n\n{path}"))
                {
                    return;
                }

                try
                {
                    _store.CreateDirectory(Path.GetDirectoryName(path)!);
                    _store.CreateFile(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _dialogs.ShowError(title, $"파일을 만들 수 없습니다.\n\n{ex.Message}");
                    return;
                }
            }

            paths = [path];
        }

        var added = new List<string>();
        foreach (var path in paths)
        {
            if (await AddCustomFileAsync(owner, path) is { } error)
            {
                _dialogs.ShowError(title, paths.Count > 1 ? $"{Path.GetFileName(path)}: {error}" : error);
            }
            else
            {
                added.Add(path);
            }
        }

        if (owner is null)
        {
            await RefreshSystemExplorerAsync();
        }

        if (added.Count == 0)
        {
            return;
        }

        StatusMessage = added.Count == 1
            ? $"사용자 지정 파일에 등록했습니다: {added[0]}"
            : $"사용자 지정 파일에 {added.Count}개를 등록했습니다.";
        if (!browse)
        {
            await OpenFileAsync(added[0], preview: false);
        }
    }

    /// <summary>Returns an error message, or null when the file is on the list.</summary>
    private async Task<string?> AddCustomFileAsync(ProjectDefinition? owner, string path)
    {
        if (owner is null)
        {
            return new ImportantFiles(_store, ImportantFiles.ListFileFor(Paths)).Add(path);
        }

        if (CustomFileRules.Validate(path) is { } error)
        {
            return error;
        }

        var id = owner.Id;
        return await Projects.ChangeRegistryAsync(r => r.AddCustomFile(id, path)) ? null : "프로젝트 목록에 저장하지 못했습니다.";
    }

    private string? ValidateOtherFilePath(string text)
    {
        var raw = System.Environment.ExpandEnvironmentVariables(text.Trim().Trim('"'));
        if (raw.Length == 0)
        {
            return "경로를 입력하세요.";
        }

        if (!Path.IsPathFullyQualified(raw))
        {
            return "전체 경로를 입력하세요(예: C:\\Users\\me\\.wslconfig, %USERPROFILE%\\.wslconfig).";
        }

        var name = Path.GetFileName(raw);
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || Path.GetDirectoryName(raw)?.IndexOfAny([.. Path.GetInvalidPathChars(), '*', '?', '|', '<', '>', '"']) >= 0)
        {
            return "경로에 사용할 수 없는 문자가 있습니다.";
        }

        try
        {
            _ = Path.GetFullPath(raw);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"올바른 경로가 아닙니다: {ex.Message}";
        }

        if (_store.DirectoryExists(raw))
        {
            return "폴더가 아닌 파일 경로를 입력하세요.";
        }

        return SecretFilePolicy.IsSecret(Path.GetFileName(raw)) ? "비밀 파일은 등록할 수 없습니다." : null;
    }

    /// <summary>Main menu: takes the current file off every "사용자 지정 파일" list that has it.</summary>
    [RelayCommand]
    private async Task RemoveImportantFileAsync()
    {
        if (ActiveTextDocument?.FilePath is not { } path)
        {
            return;
        }

        var owners = Projects.Explorers.Where(e => e.SupportsVisibility && e.HasCustomFile(path)).Select(e => (ProjectDefinition?)e.Project).ToList();
        var system = new ImportantFiles(_store, ImportantFiles.ListFileFor(Paths));
        if (system.List(ImportantFiles.DefaultsFor(Paths)).Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            owners.Add(null);
        }

        if (owners.Count == 0)
        {
            _dialogs.ShowInfo("사용자 지정 파일", "이 파일은 사용자 지정 파일 목록에 없습니다.");
            return;
        }

        foreach (var owner in owners)
        {
            await RemoveCustomFileAsync(owner, path);
        }
    }

    /// <summary>Takes a file off a "사용자 지정 파일" list (the file itself is not touched).</summary>
    private async Task RemoveCustomFileAsync(ProjectDefinition? owner, string path)
    {
        if (owner is null)
        {
            var list = new ImportantFiles(_store, ImportantFiles.ListFileFor(Paths));
            if (!list.List(ImportantFiles.DefaultsFor(Paths)).Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                _dialogs.ShowInfo("사용자 지정 파일", "이 파일은 사용자 지정 파일 목록에 없습니다.");
                return;
            }

            list.Remove(path);
            await RefreshSystemExplorerAsync();
        }
        else
        {
            var id = owner.Id;
            if (!await Projects.ChangeRegistryAsync(r => r.RemoveCustomFile(id, path)))
            {
                return;
            }
        }

        StatusMessage = $"사용자 지정 파일 목록에서 뺐습니다(파일은 그대로 둡니다): {Path.GetFileName(path)}";
    }

    /// <summary>Raised after the settings changed; the view applies theme and font size.</summary>
    public event EventHandler<DotDeck.Core.Settings.AppSettings>? SettingsChanged;

    [RelayCommand]
    private void OpenSettings()
    {
        var store = new DotDeck.Core.Settings.AppSettingsStore(_store, Paths.SettingsFile);
        if (_dialogs.ShowSettings(store.Load(), Paths.BackupsDir) is not { } settings)
        {
            return;
        }

        try
        {
            store.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError("설정", $"설정을 저장하지 못했습니다.\n\n{ex.Message}");
        }

        _backups.Retention = settings.BackupRetention;
        Core.Security.SecretPresentation.MaskByDefault = settings.MaskSecretsByDefault;
        foreach (var document in Documents.OfType<Documents.TextDocumentViewModel>()) document.IsSecretsRevealed = !settings.MaskSecretsByDefault;
        _backups.BackupsDirectory = settings.BackupDirectory ?? Paths.BackupsDir;
        ExplorerPreferences.SetGlobalExtensions(settings.DefaultExtensions);
        SettingsChanged?.Invoke(this, settings);
        StatusMessage = "설정을 적용했습니다.";
    }

    /// <summary>Zips chosen projects to a user-chosen folder (secret files, links and excluded folders left out).</summary>
    [RelayCommand]
    private async Task BackupProjectsAsync()
    {
        var roots = Projects.Explorers
            .Where(e => e.Source.RootPath is not null)
            .Select(e => (e.Id, e.Source.Project.Name, Root: e.Source.RootPath!, Excluded: ExcludedFolderPolicy.For(e.Source.Project)))
            .ToList();
        var store = new DotDeck.Core.Settings.AppSettingsStore(_store, Paths.SettingsFile);
        var settings = store.Load();
        if (_dialogs.ShowProjectBackup(roots.Select(r => (r.Id, r.Name, r.Root)).ToList(), settings.ProjectBackupDirectory) is not { } choice)
        {
            return;
        }

        var selected = roots.Where(r => choice.ProjectIds.Contains(r.Id))
            .Select(r => new DotDeck.Core.Documents.SearchRoot(r.Name, r.Root, r.Excluded))
            .ToList();
        StatusMessage = "프로젝트를 백업하는 중…";
        try
        {
            var result = await Task.Run(() => DotDeck.Core.Documents.ProjectBackup.CreateZip(_store, selected, choice.Destination, TimeProvider.System));
            try
            {
                store.Save(settings with { ProjectBackupDirectory = choice.Destination });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warning($"설정을 저장하지 못했습니다: {ex.Message}");
            }

            var skipped = result.SkippedSecrets > 0 ? $"\n비밀 파일 {result.SkippedSecrets}개는 넣지 않았습니다." : string.Empty;
            StatusMessage = $"프로젝트 백업 완료: {result.ZipPath}";
            _dialogs.ShowInfo("프로젝트 백업", $"{selected.Count}개 프로젝트, 파일 {result.FileCount}개를 백업했습니다.\n\n{result.ZipPath}{skipped}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusMessage = "프로젝트 백업 실패";
            _dialogs.ShowError("프로젝트 백업", $"백업하지 못했습니다.\n\n{ex.Message}");
        }
    }

    [RelayCommand]
    private void ShowGlobalSearch()
    {
        SearchResults.IsVisible = true;
        SearchResults.IsActive = true;
        SearchResults.RequestFocus();
    }

    private IReadOnlyList<DotDeck.Core.Documents.SearchRoot> SearchRoots() =>
        Projects.Explorers
            .Where(e => e.Source.RootPath is not null)
            .Select(e => new DotDeck.Core.Documents.SearchRoot(e.Source.Project.Name, e.Source.RootPath!, ExcludedFolderPolicy.For(e.Source.Project)))
            .ToList();

    private async Task OpenSearchHitAsync(DotDeck.Core.Documents.SearchHit hit)
    {
        await OpenFileAsync(hit.Path, preview: false);
        if (ActiveContent is TextDocumentViewModel text && string.Equals(text.FilePath, hit.Path, StringComparison.OrdinalIgnoreCase))
        {
            text.GoToLine(hit.Line);
        }
    }

    private async Task RefreshSystemExplorerAsync()
    {
        foreach (var explorer in Projects.Explorers.Where(e => e.Source.Project.Builtin == BuiltinProject.System))
        {
            await explorer.LoadAsync();
        }
    }
}
