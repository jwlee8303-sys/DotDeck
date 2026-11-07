using System.IO;
using CommunityToolkit.Mvvm.Input;
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
        _environment, _store, Paths, _dialogs, message => StatusMessage = message, RestartAsAdministrator);

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

    [RelayCommand]
    private async Task AddImportantFileAsync()
    {
        var picked = _dialogs.PickOpenFiles("중요 파일 추가", Paths.Home);
        if (picked.Count == 0)
        {
            return;
        }

        var list = new ImportantFiles(_store, ImportantFiles.ListFileFor(Paths));
        foreach (var path in picked)
        {
            if (list.Add(path) is { } error)
            {
                _dialogs.ShowError("중요 파일", $"{Path.GetFileName(path)}: {error}");
            }
        }

        await RefreshSystemExplorerAsync();
    }

    [RelayCommand]
    private async Task RemoveImportantFileAsync()
    {
        if (ActiveTextDocument?.FilePath is not { } path)
        {
            return;
        }

        var list = new ImportantFiles(_store, ImportantFiles.ListFileFor(Paths));
        if (!list.List(ImportantFiles.DefaultsFor(Paths)).Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            _dialogs.ShowInfo("중요 파일", "현재 파일은 중요 파일 목록에 없습니다.");
            return;
        }

        list.Remove(path);
        await RefreshSystemExplorerAsync();
        StatusMessage = $"중요 파일 목록에서 뺐습니다: {Path.GetFileName(path)}";
    }

    private async Task RefreshSystemExplorerAsync()
    {
        foreach (var explorer in Projects.Explorers.Where(e => e.Source.Project.Builtin == BuiltinProject.System))
        {
            await explorer.LoadAsync();
        }
    }
}
