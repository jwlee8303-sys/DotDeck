using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using DotDeck.App.ViewModels.Dialogs;
using DotDeck.Core.Explorer;
using DotDeck.Core.Logging;
using DotDeck.Core.Projects;

namespace DotDeck.App.ViewModels.Explorer;

/// <summary>
/// Project area (SPEC 2): every visible, attached project as a section of one flattened, virtualized list.
/// Owns project management (add/edit/remove/hide/order) and detaching explorers into their own panes.
/// </summary>
public sealed partial class ProjectsPaneViewModel : ToolPaneViewModel
{
    private readonly ExplorerContext _context;
    private readonly SessionStateStore _session;
    private readonly Action<ToolPaneViewModel> _addTool;
    private readonly Action<ToolPaneViewModel> _removeTool;
    private readonly SynchronizationContext _ui;
    private readonly List<ExplorerViewModel> _explorers = [];
    private readonly Dictionary<string, DetachedExplorerPaneViewModel> _detached = new(StringComparer.Ordinal);
    private Dictionary<string, ExplorerViewState> _savedStates;
    private bool _initialized;

    public ProjectsPaneViewModel(
        ExplorerContext context,
        SessionStateStore session,
        Action<ToolPaneViewModel> addTool,
        Action<ToolPaneViewModel> removeTool)
        : base("tool.projects", "프로젝트", ToolLocation.Left)
    {
        _context = context;
        _session = session;
        _addTool = addTool;
        _removeTool = removeTool;
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        List = new ExplorerListViewModel(context);
        _savedStates = new Dictionary<string, ExplorerViewState>(session.LoadExplorers(), StringComparer.Ordinal);
    }

    /// <summary>The project area's list (attached explorers).</summary>
    public ExplorerListViewModel List { get; }

    public IReadOnlyList<ExplorerViewModel> Explorers => _explorers;

    /// <summary>Hidden projects, for "숨긴 프로젝트 표시".</summary>
    public ObservableCollection<ProjectDefinition> HiddenProjects { get; } = [];

    private ProjectRegistry Registry => _context.Registry.Registry;

    /// <summary>
    /// Creates the panes of explorers that were detached last time, before the docking layout is restored,
    /// so the layout can find them by ContentId. Their explorers are filled in by <see cref="InitializeAsync"/>.
    /// </summary>
    public void PrepareDetachedPanes()
    {
        foreach (var (id, state) in _savedStates)
        {
            if (state.IsDetached && Registry.Find(id) is { Hidden: false } project && !_detached.ContainsKey(id))
            {
                _addTool(CreateDetachedPane(project));
            }
        }
    }

    /// <summary>Shows every visible project and loads the trees (SPEC 2: auto open).</summary>
    public async Task InitializeAsync()
    {
        _initialized = true;
        Registry.Changed += OnRegistryChanged;
        await SyncProjectsAsync();

        // Panes prepared for projects that did not come back (removed/hidden meanwhile) are dropped.
        foreach (var pane in _detached.Values.Where(p => p.Explorer is null).ToList())
        {
            CloseDetachedPane(pane);
        }
    }

    public void SaveState()
    {
        if (!_initialized)
        {
            return;
        }

        foreach (var explorer in _explorers)
        {
            _savedStates[explorer.Id] = explorer.GetState();
        }

        // Keep hidden projects' state so showing them again restores it; drop removed projects.
        foreach (var id in _savedStates.Keys.ToList())
        {
            if (Registry.Find(id) is null)
            {
                _savedStates.Remove(id);
            }
        }

        _session.SaveExplorers(_savedStates);
    }

    // ===== Project management =====

    [RelayCommand]
    private void AddProject()
    {
        var dialog = new ProjectDialogViewModel(
            "프로젝트 추가",
            existing: null,
            builtinRoot: null,
            commit: result => ChangeRegistryForDialogAsync(r => r.AddFolder(result.Name, result.Root, result.Color, result.Filter)),
            pickFolder: initial => _context.Dialogs.PickFolder("프로젝트 폴더 선택", initial),
            directoryExists: _context.Store.DirectoryExists);
        if (_context.Dialogs.ShowProjectDialog(dialog))
        {
            _context.SetStatus($"프로젝트를 추가했습니다: {dialog.Name.Trim()}");
        }
    }

    public Task EditProjectAsync(ExplorerViewModel explorer)
    {
        var project = explorer.Project;
        var dialog = new ProjectDialogViewModel(
            "프로젝트 편집",
            project,
            builtinRoot: explorer.Source.RootPath,
            commit: result => ChangeRegistryForDialogAsync(r => r.Update(
                project.Id,
                project.IsBuiltin ? null : result.Name,
                project.IsBuiltin ? null : result.Root,
                result.Color,
                result.Filter)),
            pickFolder: initial => _context.Dialogs.PickFolder("프로젝트 폴더 선택", initial),
            directoryExists: _context.Store.DirectoryExists);
        _context.Dialogs.ShowProjectDialog(dialog);
        return Task.CompletedTask;
    }

    public async Task RemoveProjectAsync(ExplorerViewModel explorer)
    {
        if (explorer.IsBuiltin)
        {
            _context.SetStatus("기본 제공 프로젝트는 제거할 수 없습니다. 숨기기를 사용하세요.");
            return;
        }

        var message = $"'{explorer.Project.Name}' 프로젝트를 목록에서 제거할까요?\n\n폴더와 파일은 디스크에 그대로 남습니다.\n{explorer.Project.Root}";
        if (_context.Dialogs.Confirm("프로젝트 제거", message))
        {
            var id = explorer.Id;
            await ChangeRegistryAsync(r => r.Remove(id));
        }
    }

    public Task MoveProjectAsync(ExplorerViewModel explorer, int delta)
    {
        var id = explorer.Id;

        // The target is computed inside the serialized change, against the order left by any earlier queued
        // move, so rapid clicks each move exactly one step.
        return ChangeRegistryAsync(r =>
        {
            var ordered = r.Projects;
            var index = ordered.ToList().FindIndex(p => p.Id == id);
            if (index < 0)
            {
                return;
            }

            // Skip over hidden projects so one step always moves past a visible neighbor.
            var target = index + delta;
            while (target >= 0 && target < ordered.Count && ordered[target].Hidden)
            {
                target += delta;
            }

            if (target >= 0 && target < ordered.Count)
            {
                r.MoveTo(id, target);
            }
        });
    }

    [RelayCommand]
    private Task ShowProjectAsync(string? id) =>
        id is null ? Task.CompletedTask : ChangeRegistryAsync(r => r.SetHidden(id, false));

    /// <summary>Runs a registry change off the UI thread; failures become a status message.</summary>
    public async Task<bool> ChangeRegistryAsync(Action<ProjectRegistry> change)
    {
        var error = await ChangeRegistryForDialogAsync(r =>
        {
            change(r);
            return true;
        });
        if (error is not null)
        {
            _context.SetStatus(error);
        }

        return error is null;
    }

    /// <summary>Like <see cref="ChangeRegistryAsync"/> but returns the error message (null on success).</summary>
    private async Task<string?> ChangeRegistryForDialogAsync<T>(Func<ProjectRegistry, T> change)
    {
        try
        {
            await _context.Registry.RunAsync(change);
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
        catch (ProjectsConflictException ex)
        {
            return ex.Message;
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            return ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _context.Log.Error("프로젝트 목록을 저장하지 못했습니다.", ex);
            return $"프로젝트 목록을 저장하지 못했습니다: {ex.Message}";
        }
    }

    // ===== Detach / reattach =====

    public void Detach(ExplorerViewModel explorer)
    {
        if (explorer.IsDetached)
        {
            return;
        }

        // A pane can already exist only when it was prepared for layout restore (and is already a tool).
        var existing = _detached.GetValueOrDefault(explorer.Id);
        var pane = existing ?? CreateDetachedPane(explorer.Project);
        pane.Explorer = explorer;
        explorer.MoveToList(pane.List, 0);
        explorer.IsDetached = true;
        if (existing is null)
        {
            _addTool(pane);
        }

        pane.IsVisible = true;
        pane.IsActive = true;
    }

    public void Reattach(ExplorerViewModel explorer)
    {
        if (!explorer.IsDetached)
        {
            return;
        }

        explorer.MoveToList(List, AttachIndexOf(explorer));
        explorer.IsDetached = false;
        if (_detached.GetValueOrDefault(explorer.Id) is { } pane)
        {
            CloseDetachedPane(pane);
        }

        IsVisible = true;
        IsActive = true;
    }

    private DetachedExplorerPaneViewModel CreateDetachedPane(ProjectDefinition project)
    {
        var pane = new DetachedExplorerPaneViewModel(project.Id, project.Name, new ExplorerListViewModel(_context), OnDetachedPaneClosed);
        _detached[project.Id] = pane;
        return pane;
    }

    private void OnDetachedPaneClosed(DetachedExplorerPaneViewModel pane)
    {
        // Called from AvalonDock's hide; change the source collection after it finishes.
        _ui.Post(_ =>
        {
            if (pane.Explorer is { IsDetached: true } explorer)
            {
                Reattach(explorer);
            }
            else
            {
                CloseDetachedPane(pane);
            }
        }, null);
    }

    private void CloseDetachedPane(DetachedExplorerPaneViewModel pane)
    {
        _detached.Remove(pane.ProjectId);
        pane.IsClosingByApp = true;
        pane.Explorer = null;
        _removeTool(pane);
    }

    /// <summary>Index in the project area where an explorer belongs, by registry order.</summary>
    private int AttachIndexOf(ExplorerViewModel explorer)
    {
        var order = Registry.Projects.Select(p => p.Id).ToList();
        var position = order.IndexOf(explorer.Id);
        foreach (var header in List.Rows.OfType<ProjectHeaderRowViewModel>())
        {
            if (order.IndexOf(header.Explorer.Id) > position)
            {
                return List.Rows.IndexOf(header);
            }
        }

        return List.Rows.Count;
    }

    // ===== Registry synchronization =====

    private void OnRegistryChanged(object? sender, EventArgs e) => _ui.Post(async _ =>
    {
        try
        {
            await SyncProjectsAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _context.Log.Error("프로젝트 목록을 갱신하지 못했습니다.", ex);
        }
    }, null);

    /// <summary>Creates, updates, removes and reorders explorers to match the registry.</summary>
    private async Task SyncProjectsAsync()
    {
        var projects = Registry.Projects;
        var wanted = projects.Where(p => !p.Hidden).ToList();
        var loads = new List<Task>();

        HiddenProjects.Clear();
        foreach (var hidden in projects.Where(p => p.Hidden))
        {
            HiddenProjects.Add(hidden);
        }

        foreach (var explorer in _explorers.Where(x => !wanted.Any(p => p.Id == x.Id)).ToList())
        {
            // Remember the state so the project reopens the same way when shown again.
            _savedStates[explorer.Id] = explorer.GetState() with { IsDetached = false };
            explorer.RemoveRows();
            if (_detached.GetValueOrDefault(explorer.Id) is { } pane)
            {
                CloseDetachedPane(pane);
            }

            _explorers.Remove(explorer);
        }

        var ordered = new List<ExplorerViewModel>();
        foreach (var project in wanted)
        {
            var existing = _explorers.FirstOrDefault(x => x.Id == project.Id);
            if (existing is not null)
            {
                if (!Equals(existing.Project, project))
                {
                    loads.Add(existing.UpdateProjectAsync(project));
                    if (_detached.GetValueOrDefault(project.Id) is { } pane)
                    {
                        pane.Title = project.Name;
                    }
                }

                ordered.Add(existing);
                continue;
            }

            var state = _savedStates.GetValueOrDefault(project.Id, ExplorerViewState.Default);
            var detachedPane = _detached.GetValueOrDefault(project.Id);
            ExplorerViewModel created;
            try
            {
                created = new ExplorerViewModel(_context, this, detachedPane?.List ?? List, project, state);
            }
            catch (ArgumentException ex)
            {
                _context.Log.Error($"프로젝트를 표시할 수 없습니다: {project.Name}", ex);
                continue;
            }

            if (detachedPane is not null)
            {
                detachedPane.Explorer = created;
                created.IsDetached = true;
                detachedPane.List.Rows.Add(created.Header);
            }

            ordered.Add(created);
            loads.Add(created.LoadAsync());
        }

        _explorers.Clear();
        _explorers.AddRange(ordered);
        ApplyOrder(ordered.Where(x => !x.IsDetached).ToList());
        await Task.WhenAll(loads);
    }

    /// <summary>Lays the attached segments out in registry order, moving only when needed.</summary>
    private void ApplyOrder(List<ExplorerViewModel> ordered)
    {
        var current = List.Rows.OfType<ProjectHeaderRowViewModel>().Select(h => h.Explorer).ToList();
        if (ordered.Take(current.Count).SequenceEqual(current))
        {
            // Same order; just append new explorers (headers only, bodies load asynchronously).
            foreach (var explorer in ordered.Skip(current.Count))
            {
                List.Rows.Add(explorer.Header);
            }

            return;
        }

        var segments = current.ToDictionary(x => x, x => x.SnapshotRows());
        var selected = List.SelectedRow;
        List.Rows.Clear();
        foreach (var explorer in ordered)
        {
            foreach (var row in segments.TryGetValue(explorer, out var rows) ? rows : [explorer.Header])
            {
                List.Rows.Add(row);
            }
        }

        if (selected is not null && List.Rows.Contains(selected))
        {
            List.Select(selected);
        }
    }
}
