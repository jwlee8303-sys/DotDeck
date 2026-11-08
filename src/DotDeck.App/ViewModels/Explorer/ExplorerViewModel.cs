using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotDeck.App.Services;
using DotDeck.Core.Explorer;
using DotDeck.Core.Explorer.Visibility;
using DotDeck.Core.FileOperations;
using DotDeck.Core.Files;
using DotDeck.Core.Logging;
using DotDeck.Core.Paths;
using DotDeck.Core.Projects;

namespace DotDeck.App.ViewModels.Explorer;

/// <summary>Services shared by all explorers.</summary>
public sealed record ExplorerContext(
    ProjectRegistryGateway Registry,
    AppPaths Paths,
    IFileStore Store,
    IClipboardService Clipboard,
    IShellLauncher Shell,
    IDialogService Dialogs,
    ExplorerFileOperations FileOps,
    IAppLog Log,
    Action<string> SetStatus,
    IFolderWatcherFactory Watchers,
    ExplorerPreferences Preferences,
    Func<string, bool, Task> OpenFile,
    Action<string>? OpenSystemNode = null,
    Func<ProjectDefinition?, bool, Task>? RegisterCustomFile = null,
    Func<ProjectDefinition?, string, Task>? UnregisterCustomFile = null,
    Func<string, DotDeck.Core.Editors.EditorKind?, Task>? OpenAs = null);

/// <summary>
/// One project's explorer. Its rows live in a list (<see cref="List"/>: the project area, or its own detached
/// pane) as a contiguous segment that starts with <see cref="Header"/>. File system listing runs on the thread
/// pool; results are applied on the UI thread and discarded if a newer load started meanwhile.
/// </summary>
public sealed partial class ExplorerViewModel : ObservableObject
{
    private readonly ExplorerContext _context;
    private readonly ProjectsPaneViewModel _owner;
    private readonly HashSet<string> _expanded = new(ExplorerTreeBuilder.KeyComparer);
    private int _loadVersion;
    private string? _pendingSelectionKey;
    private string? _selectionOverride;
    private readonly SynchronizationContext? _ui;
    private IDisposable? _watcher;
    private string? _watchedRoot;
    private bool _removed;
    private bool _creatingWatcher;
    private bool _watchDirty;
    private bool _watchRefreshing;

    public ExplorerViewModel(
        ExplorerContext context,
        ProjectsPaneViewModel owner,
        ExplorerListViewModel list,
        ProjectDefinition project,
        ExplorerViewState state)
    {
        _context = context;
        _owner = owner;
        List = list;
        Header = new ProjectHeaderRowViewModel(this) { IsCollapsed = state.IsCollapsed };
        _expanded.UnionWith(state.Expanded);
        _pendingSelectionKey = state.Selected;
        Project = project;
        Source = ProjectSources.Create(project, context.Paths, context.Store);
        ApplyHeader();
        _ui = SynchronizationContext.Current;
    }

    /// <summary>The list currently showing this explorer.</summary>
    public ExplorerListViewModel List { get; private set; }

    private ObservableCollection<ExplorerRowViewModel> Rows => List.Rows;

    /// <summary>True while shown in its own tool pane instead of the project area.</summary>
    [ObservableProperty]
    private bool _isDetached;

    public bool IsBuiltin => Project.IsBuiltin;

    public ProjectDefinition Project { get; private set; }

    public IProjectSource Source { get; private set; }

    public ProjectHeaderRowViewModel Header { get; }

    public string Id => Project.Id;

    /// <summary>Rows of this explorer below the header, in display order.</summary>
    public IEnumerable<ExplorerRowViewModel> BodyRows
    {
        get
        {
            var (start, end) = Segment();
            for (var i = start; i < end; i++)
            {
                yield return Rows[i];
            }
        }
    }

    public ExplorerViewState GetState()
    {
        var selectedKey = List.SelectedRow is NodeRowViewModel node && node.Explorer == this ? node.Key : _pendingSelectionKey;
        return new ExplorerViewState(Header.IsCollapsed, [.. _expanded], selectedKey, IsDetached);
    }

    /// <summary>Applies a changed definition (from the registry). Reloads when the tree content could differ.</summary>
    public async Task UpdateProjectAsync(ProjectDefinition project)
    {
        var old = Project;
        Project = project;
        ApplyHeader();
        var sourceChanged = !string.Equals(old.Root, project.Root, StringComparison.OrdinalIgnoreCase)
            || !SameList(old.ExcludedFolders, project.ExcludedFolders);
        var treeChanged = sourceChanged
            || !old.Visibility.ContentEquals(project.Visibility)
            || !old.View.ContentEquals(project.View)
            || !old.CustomFiles.SequenceEqual(project.CustomFiles, StringComparer.OrdinalIgnoreCase);
        if (sourceChanged)
        {
            Source = ProjectSources.Create(project, _context.Paths, _context.Store);
        }

        // An open edit session keeps its (unsaved) checks; extension settings changed elsewhere apply to it.
        if (_editor is not null && !old.Visibility.ContentEquals(project.Visibility))
        {
            var session = _editor.Current;
            _editor = new VisibilityEditor(project.Visibility with { Rules = session.Rules, HideCustomFiles = session.HideCustomFiles }, Extensions);
        }

        if (treeChanged)
        {
            ApplyHeader();
            await LoadAsync();
        }
    }

    /// <summary>(Re)builds the whole body from the file system, keeping expansion and selection.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        var version = ++_loadVersion;
        if (Header.IsCollapsed)
        {
            ReplaceBody([]);
            return;
        }

        Header.IsLoading = true;
        try
        {
            TreeBuildResult result;
            IReadOnlyList<TreeRow> customRows;
            IProjectSource source;
            while (true)
            {
                var expanded = new HashSet<string>(_expanded, ExplorerTreeBuilder.KeyComparer);
                source = BuildSource();
                var current = source;
                (result, customRows) = await Task.Run(() =>
                {
                    var built = ExplorerTreeBuilder.Build(current, expanded);

                    // No root listing: the "사용자 지정 파일" node (files anywhere) is still shown above the message.
                    var custom = current is CustomFilesProjectSource wrapper && (built.RootState == ProjectRootState.Missing || built.RootError is not null)
                        ? ExplorerTreeBuilder.Build(wrapper.NodeOnly(), expanded).Rows
                        : [];
                    return (built, custom);
                });
                if (version != _loadVersion)
                {
                    return;
                }

                // The user expanded/collapsed something meanwhile: build again from the current state
                // instead of applying the old snapshot.
                if (!_expanded.SetEquals(expanded))
                {
                    continue;
                }

                // Replacing the rows would drop an in-place editor (rename / new item) the user started
                // meanwhile: wait until it ends, then list again so the result is current.
                if (BodyRows.OfType<IEditableRow>().Any(r => r.IsEditing))
                {
                    while (BodyRows.OfType<IEditableRow>().Any(r => r.IsEditing))
                    {
                        await Task.Delay(300);
                        if (version != _loadVersion)
                        {
                            return;
                        }
                    }

                    continue;
                }

                break;
            }

            // A refresh replaces the row objects: carry the selection as it is right now over by key,
            // unless a file operation asked to select its result.
            if (_selectionOverride is { } requested)
            {
                _pendingSelectionKey = requested;
                _selectionOverride = null;
            }
            else if (BodyRows.OfType<NodeRowViewModel>().FirstOrDefault(r => r.IsSelected) is { } selected)
            {
                _pendingSelectionKey = selected.Key;
            }

            // A filtered layout does not list every folder: only an unfiltered listing tells which keys are gone.
            if (CustomFilesProjectSource.Unwrap(source) is not VisibleProjectSource)
            {
                _expanded.ExceptWith(result.StaleExpandedKeys);
            }

            ReplaceBody([.. customRows.Select(CreateNodeRow), .. CreateBody(result, CustomFilesProjectSource.Unwrap(source) is VisibleProjectSource)]);
            if (CustomFilesProjectSource.Unwrap(source) is VisibleProjectSource { IsTruncated: true })
            {
                _context.SetStatus($"{Project.Name}: 항목이 너무 많아 일부 폴더는 내용을 확인하지 않고 표시했습니다.");
            }

            RestoreSelection();
            UpdateWatcher();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (version == _loadVersion)
            {
                _context.Log.Error($"프로젝트 트리를 불러오지 못했습니다: {Project.Name}", ex);
                ReplaceBody([new MessageRowViewModel(this, $"트리를 불러오지 못했습니다: {ex.Message}", isError: true)]);
            }
        }
        finally
        {
            if (version == _loadVersion)
            {
                Header.IsLoading = false;
            }
        }
    }

    /// <summary>True when <paramref name="path"/> is this project's root or inside it.</summary>
    public bool ContainsPath(string path)
    {
        if (Source.RootPath is not { } root)
        {
            return false;
        }

        var full = Path.TrimEndingDirectorySeparator(path);
        var rootFull = Path.TrimEndingDirectorySeparator(root);

        // A drive root ("D:\") keeps its separator, so only add one when it is missing.
        var prefix = Path.EndsInDirectorySeparator(rootFull) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        return string.Equals(full, rootFull, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Tree key (path relative to the root) of a path inside this project, or null.</summary>
    public string? KeyOfPath(string path)
    {
        if (Source.RootPath is not { } root || !ContainsPath(path))
        {
            return null;
        }

        var relative = Path.GetRelativePath(root, path);
        return relative == "." ? null : relative;
    }

    /// <summary>
    /// Reloads after a file operation. When <paramref name="selectPath"/> is inside this project, its folders are
    /// expanded so it is visible and it becomes the selection.
    /// </summary>
    public Task RefreshAfterOperationAsync(string? selectPath)
    {
        if (selectPath is not null && KeyOfPath(selectPath) is { } key)
        {
            for (var parent = Path.GetDirectoryName(key); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
            {
                _expanded.Add(parent);
            }

            _selectionOverride = key;
        }

        return LoadAsync();
    }

    /// <summary>
    /// Inserts a row for typing a new item's name inside <paramref name="parent"/> (a folder row, or null for the
    /// project root). The folder is expanded first so the new row appears among its children.
    /// </summary>
    /// <param name="sibling">
    /// A row the placeholder goes right after when <paramref name="directory"/> has no row of its own (a file of the
    /// flat view, a pinned file, a folder inside a merged compact row).
    /// </param>
    public async Task<NewItemRowViewModel?> BeginNewItemAsync(NodeRowViewModel? parent, bool isFolder, string? directory = null, NodeRowViewModel? sibling = null)
    {
        directory ??= parent?.TargetNode.FullPath ?? Source.RootPath;
        // Off the UI thread: a disconnected network folder can block for a long time.
        var store = _context.Store;
        if (directory is null || !await Task.Run(() => store.DirectoryExists(directory)))
        {
            _context.SetStatus("이 위치에는 새 항목을 만들 수 없습니다.");
            return null;
        }

        if (Header.IsCollapsed)
        {
            await SetCollapsedAsync(false);
        }

        if (parent is not null)
        {
            await ExpandAsync(parent);
            if (!parent.IsExpanded || Rows.IndexOf(parent) < 0)
            {
                return null;
            }
        }

        // Only one pending new item at a time.
        foreach (var pending in Rows.OfType<NewItemRowViewModel>().ToList())
        {
            Rows.Remove(pending);
        }

        var anchor = sibling is not null && Rows.IndexOf(sibling) is >= 0 and var at ? at
            : parent is null ? Rows.IndexOf(Header) : Rows.IndexOf(parent);
        if (anchor < 0)
        {
            return null;
        }

        // An "(비어 있음)" message would sit next to the new row: drop it.
        if (anchor + 1 < Rows.Count && Rows[anchor + 1] is MessageRowViewModel { Action: null } message && message.Explorer == this)
        {
            Rows.Remove(message);
        }

        var depth = sibling is not null ? sibling.Depth : parent is null ? 0 : parent.Depth + 1;
        var row = new NewItemRowViewModel(this, depth, directory, isFolder);
        Rows.Insert(anchor + 1, row);
        List.Select(row);
        List.RequestEditorFocus(row);
        return row;
    }

    public void RemoveRow(ExplorerRowViewModel row) => Rows.Remove(row);

    [RelayCommand]
    private async Task ToggleCollapsedAsync()
    {
        Header.IsCollapsed = !Header.IsCollapsed;
        await LoadAsync();
    }

    public async Task SetCollapsedAsync(bool collapsed)
    {
        if (Header.IsCollapsed != collapsed)
        {
            await ToggleCollapsedAsync();
        }
    }

    [RelayCommand]
    public async Task ToggleNodeAsync(NodeRowViewModel? row)
    {
        if (row is null || !row.IsFolder || row.IsLoading)
        {
            return;
        }

        if (row.IsExpanded)
        {
            Collapse(row);
            return;
        }

        _expanded.Add(row.Key);
        row.IsLoading = true;
        var version = _loadVersion;
        try
        {
            var expanded = new HashSet<string>(_expanded, ExplorerTreeBuilder.KeyComparer);
            var stale = new HashSet<string>(ExplorerTreeBuilder.KeyComparer);
            var source = BuildSource();
            var children = await Task.Run(() => ExplorerTreeBuilder.BuildChildren(source, row.Node, row.Depth, expanded, stale));
            if (version != _loadVersion || row.IsExpanded || Rows.IndexOf(row) is var index && index < 0)
            {
                return;
            }

            if (CustomFilesProjectSource.Unwrap(source) is not VisibleProjectSource)
            {
                _expanded.ExceptWith(stale);
            }

            row.Error = null;
            row.IsExpanded = true;
            row.IsKnownEmpty = children.Count == 0;
            var i = Rows.IndexOf(row) + 1;
            foreach (var child in children)
            {
                Rows.Insert(i++, CreateNodeRow(child));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stale attempt (row replaced by a refresh, or a newer load) must not touch current state.
            if (version == _loadVersion && Rows.Contains(row) && !row.IsExpanded)
            {
                _expanded.Remove(row.Key);
                row.Error = ex.Message;
                _context.SetStatus($"폴더를 열 수 없습니다: {ex.Message}");
            }
        }
        finally
        {
            row.IsLoading = false;
        }
    }

    public async Task ExpandAsync(NodeRowViewModel row)
    {
        if (!row.IsExpanded)
        {
            await ToggleNodeAsync(row);
        }
    }

    public void Collapse(NodeRowViewModel row)
    {
        var index = Rows.IndexOf(row);
        if (index < 0)
        {
            return;
        }

        // Nested expansion is remembered (VS behavior): only this folder's key is dropped.
        _expanded.Remove(row.Key);
        row.IsExpanded = false;
        var hidSelection = false;
        while (index + 1 < Rows.Count && Rows[index + 1] is (NodeRowViewModel or NewItemRowViewModel) and var child
               && child.Explorer == this && child.Depth > row.Depth)
        {
            hidSelection |= child.IsSelected;
            Rows.RemoveAt(index + 1);
        }

        // Like a tree view: a selection inside the collapsed folder moves to the folder.
        if (hidSelection)
        {
            List.RequestSelection(this, row);
        }
    }

    [RelayCommand]
    private async Task CollapseAllAsync()
    {
        _expanded.Clear();
        if (Header.IsLoading)
        {
            // A load in flight would re-expand from its old snapshot: supersede it with a collapsed one.
            await LoadAsync();
            return;
        }

        // Invalidate pending folder expansions so a late result cannot reopen anything.
        ++_loadVersion;
        foreach (var row in BodyRows.OfType<NodeRowViewModel>().Where(r => r.Depth == 0 && r.IsExpanded).ToList())
        {
            Collapse(row);
        }
    }

    // Project management (header context menu); the project area owns dialogs and ordering.
    [RelayCommand]
    private Task EditProjectAsync() => _owner.EditProjectAsync(this);

    [RelayCommand]
    private Task HideProjectAsync() => _owner.ChangeRegistryAsync(r => r.SetHidden(Id, true));

    [RelayCommand]
    private Task MoveUpAsync() => _owner.MoveProjectAsync(this, -1);

    [RelayCommand]
    private Task MoveDownAsync() => _owner.MoveProjectAsync(this, +1);

    [RelayCommand]
    private Task RemoveProjectAsync() => _owner.RemoveProjectAsync(this);

    [RelayCommand]
    private void Detach() => _owner.Detach(this);

    [RelayCommand]
    private void Reattach() => _owner.Reattach(this);

    /// <summary>
    /// Moves this explorer's rows (header included) into <paramref name="target"/> at <paramref name="index"/>,
    /// keeping expansion, loaded rows and in-flight loads.
    /// </summary>
    public void MoveToList(ExplorerListViewModel target, int index)
    {
        var rows = SnapshotRows();
        var wasSelected = rows.FirstOrDefault(r => r.IsSelected);
        if (List.SelectedRow is { } selected && rows.Contains(selected))
        {
            List.SelectedRow = null;
        }

        RemoveSegment();
        List = target;
        for (var i = 0; i < rows.Count; i++)
        {
            target.Rows.Insert(Math.Min(index + i, target.Rows.Count), rows[i]);
        }

        if (wasSelected is not null)
        {
            target.Select(wasSelected);
        }
    }

    [RelayCommand]
    private void CopyRootPath()
    {
        if (Source.RootPath is { } root)
        {
            _context.SetStatus(_context.Clipboard.TrySetText(root) ? $"경로를 복사했습니다: {root}" : "클립보드를 사용할 수 없습니다.");
        }
    }

    [RelayCommand]
    private void RevealRoot()
    {
        if (Source.RootPath is { } root)
        {
            Reveal(root);
        }
    }

    public void Reveal(string path)
    {
        try
        {
            _context.Shell.RevealInExplorer(path);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _context.Log.Error($"탐색기를 열지 못했습니다: {path}", ex);
            _context.SetStatus($"탐색기를 열지 못했습니다: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task CreateRootAsync()
    {
        if (Source.RootPath is not { } root)
        {
            return;
        }

        try
        {
            _context.Store.CreateDirectory(root);
            _context.Log.Info($"프로젝트 폴더를 만들었습니다: {root}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _context.SetStatus($"폴더를 만들지 못했습니다: {ex.Message}");
            return;
        }

        await LoadAsync();
    }

    /// <summary>Removes this explorer for good: pending loads are discarded and its rows leave the list.</summary>
    public void RemoveRows()
    {
        ++_loadVersion;
        _removed = true;
        _watcher?.Dispose();
        _watcher = null;
        if (List.SelectedRow?.Explorer == this)
        {
            List.SelectedRow = null;
        }

        RemoveSegment();
    }

    /// <summary>Header plus body, for re-inserting the segment when projects are reordered.</summary>
    public List<ExplorerRowViewModel> SnapshotRows() => [Header, .. BodyRows];

    private void RemoveSegment()
    {
        var start = Rows.IndexOf(Header);
        if (start < 0)
        {
            return;
        }

        var (_, end) = Segment();
        for (var i = end - 1; i >= start; i--)
        {
            Rows.RemoveAt(i);
        }
    }

    /// <summary>Watches the project root (SPEC 2.3 live refresh). Recreated when the root changes or appears.</summary>
    private async void UpdateWatcher()
    {
        var root = Source.RootPath;
        if (_removed || _creatingWatcher
            || (_watcher is not null && string.Equals(root, _watchedRoot, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _watcher?.Dispose();
        _watcher = null;
        _watchedRoot = root;
        if (root is null)
        {
            return;
        }

        _creatingWatcher = true;
        try
        {
            // FileSystemWatcher setup touches the disk (slow on network paths): off the UI thread.
            var watchers = _context.Watchers;
            var watcher = await Task.Run(() => watchers.Watch(root, () => _ui?.Post(_ => OnFolderChanged(), null)));

            // Removed or re-rooted meanwhile: this watcher is stale (a new root is retried in finally).
            if (_removed || !string.Equals(root, Source.RootPath, StringComparison.OrdinalIgnoreCase))
            {
                watcher?.Dispose();
                _watchedRoot = null;
                return;
            }

            _watcher = watcher;

            // Changes between the listing and the watcher starting were not seen: list once more.
            if (watcher is not null)
            {
                OnFolderChanged();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _context.Log.Warning($"폴더 변경 감시를 시작하지 못했습니다: {root}", ex);
        }
        finally
        {
            _creatingWatcher = false;
        }

        // The root changed while creating: watch the current one.
        if (!_removed && _watcher is null && Source.RootPath is { } current
            && !string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
        {
            UpdateWatcher();
        }
    }

    /// <summary>Debounced file system change. Changes arriving during a refresh are coalesced into one more.</summary>
    private async void OnFolderChanged()
    {
        _watchDirty = true;
        if (_watchRefreshing)
        {
            return;
        }

        _watchRefreshing = true;
        try
        {
            while (_watchDirty && !_removed)
            {
                _watchDirty = false;
                if (!Header.IsCollapsed)
                {
                    await LoadAsync();
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _context.Log.Error($"변경 감지 후 트리를 새로 고치지 못했습니다: {Project.Name}", ex);
        }
        finally
        {
            _watchRefreshing = false;
        }
    }

    private void ApplyHeader()
    {
        Header.Name = Project.Name;
        Header.Color = Project.Color;
        Header.RootPath = Source?.RootPath;
        OnPropertyChanged(nameof(IsTreeMode));
        OnPropertyChanged(nameof(IsCompactMode));
        OnPropertyChanged(nameof(IsFlatMode));
        OnPropertyChanged(nameof(IsCustomFlatSort));
    }

    private List<ExplorerRowViewModel> CreateBody(TreeBuildResult result, bool filtered)
    {
        if (result.RootState == ProjectRootState.Missing)
        {
            return [new MessageRowViewModel(this, "폴더 없음", actionText: "만들기", action: CreateRootCommand)];
        }

        if (result.RootError is { } error)
        {
            return [new MessageRowViewModel(this, $"폴더를 열 수 없습니다: {error}", isError: true)];
        }

        if (result.Rows.Count == 0)
        {
            return [filtered
                ? new MessageRowViewModel(this, "(보이는 항목 없음)", actionText: "보일 항목 고르기", action: ToggleVisibilityEditCommand)
                : new MessageRowViewModel(this, "(비어 있음)")];
        }

        return [.. result.Rows.Select(CreateNodeRow)];
    }

    private NodeRowViewModel CreateNodeRow(TreeRow row) => new(this, row.Node, row.Depth, row.IsExpanded, row.Error)
    {
        CheckState = CheckStateOf(row.Node),
    };

    // ===== Visibility (SPEC 2.3) =====

    private VisibilityEditor? _editor;

    /// <summary>Edit mode (✎): every item is listed with a three-state check box; leaving it saves the checks.</summary>
    [ObservableProperty]
    private bool _isEditingVisibility;

    /// <summary>Folder projects (the built-in Claude/Codex ones too) have visibility rules and layouts; the System project does not.</summary>
    public bool SupportsVisibility => Source is FolderProjectSource;

    public bool IsTreeMode => SupportsVisibility && Project.View.Mode == ExplorerViewMode.Tree;

    public bool IsCompactMode => SupportsVisibility && Project.View.Mode == ExplorerViewMode.Compact;

    public bool IsFlatMode => SupportsVisibility && Project.View.Mode == ExplorerViewMode.Flat;

    public bool IsCustomFlatSort => Project.View.FlatSort == FlatSort.Custom;

    /// <summary>The extensions in effect for this project (its own list or the global default).</summary>
    public IReadOnlyList<string> Extensions => Project.Visibility.EffectiveExtensions(_context.Preferences.GlobalExtensions);

    /// <summary>
    /// Normal mode: a fresh filtered layout per build (it caches listings); edit mode and the System project: everything.
    /// Folder projects get their "사용자 지정 파일" node on top, unless it was unchecked (normal mode only).
    /// </summary>
    private IProjectSource BuildSource()
    {
        var source = Source;
        if (source is not FolderProjectSource)
        {
            return source;
        }

        var layout = _editor is not null
            ? source
            : new VisibleProjectSource(source, _context.Store, new VisibilityEvaluator(Project.Visibility, Extensions), Project.View);
        return _editor is null && Project.Visibility.HideCustomFiles
            ? layout
            : new CustomFilesProjectSource(layout, _context.Store, Project.CustomFiles);
    }

    /// <summary>Edit mode check box of a row (null outside edit mode and for rows without one).</summary>
    private CheckState? CheckStateOf(NodeInfo node)
    {
        if (_editor is not { } editor)
        {
            return null;
        }

        if (node.Id == CustomFilesProjectSource.NodeId)
        {
            return editor.Current.HideCustomFiles ? CheckState.Unchecked : CheckState.Checked;
        }

        return node.RelativePath is { } key && !node.IsCustomFile ? editor.Evaluator.CheckStateOf(key, node.Kind == NodeKind.Folder) : null;
    }

    /// <summary>True when <paramref name="path"/> is on this project's "사용자 지정 파일" list.</summary>
    public bool HasCustomFile(string path) => Project.CustomFiles.Contains(path, StringComparer.OrdinalIgnoreCase);

    /// <summary>The unsaved edit-mode rules (for saving on shutdown), or null.</summary>
    public VisibilitySettings? PendingVisibility() =>
        _editor?.Current is { } current && !current.ContentEquals(Project.Visibility) ? current : null;

    /// <summary>A file operation renamed, moved or deleted items: the open edit session follows (like the saved rules).</summary>
    /// <param name="appeared">New items (created, pasted, moved in): kept visible after the session is saved.</param>
    public void RewriteEditSession(IReadOnlyList<PathChange> changes, IReadOnlyList<string> appeared)
    {
        if (_editor is null || Source.RootPath is not { } root)
        {
            return;
        }

        var (visibility, _) = ExplorerPathRewriter.Apply(_editor.Current, Project.View, root, changes);
        foreach (var path in appeared)
        {
            if (RelativeKey.FromFullPath(root, path) is { Length: > 0 } key)
            {
                var isDirectory = _context.Store.DirectoryExists(path);
                visibility = VisibilityAutoInclude.EnsureVisible(visibility, Extensions, key, isDirectory);
            }
        }

        _editor = new VisibilityEditor(visibility, Extensions);
    }

    /// <summary>The global default extensions changed.</summary>
    public async Task ReloadForSettingsAsync()
    {
        if (_editor is not null)
        {
            _editor = new VisibilityEditor(_editor.Current, Extensions);
        }

        await LoadAsync();
    }

    [RelayCommand]
    private async Task ToggleVisibilityEditAsync()
    {
        if (!SupportsVisibility)
        {
            return;
        }

        if (_editor is null)
        {
            _editor = new VisibilityEditor(Project.Visibility, Extensions);
            IsEditingVisibility = true;
            Header.CheckState = _editor.Evaluator.CheckStateOf(string.Empty, isDirectory: true);
            Header.IsCollapsed = false;
            _context.SetStatus($"{Project.Name}: 보일 파일·폴더를 체크하세요. 흐린 항목은 숨겨집니다. ✎를 다시 누르면 저장하고 끝납니다.");
            await LoadAsync();
            return;
        }

        await EndVisibilityEditAsync();
    }

    private async Task EndVisibilityEditAsync()
    {
        if (_editor is not { } editor)
        {
            return;
        }

        var settings = editor.Current;
        if (!settings.ContentEquals(Project.Visibility))
        {
            var id = Id;
            if (!await _owner.ChangeRegistryAsync(r => r.SetVisibility(id, settings)))
            {
                // Not saved (status shows why): stay in edit mode so the checks are not lost.
                return;
            }
        }

        // A file operation rewrote the session while saving: save that version too.
        if (_editor != editor)
        {
            await EndVisibilityEditAsync();
            return;
        }

        _editor = null;
        IsEditingVisibility = false;
        Header.CheckState = null;
        await LoadAsync();
        _context.SetStatus($"{Project.Name}: 보이는 항목을 저장했습니다.");
    }

    /// <summary>Check box click: checked → unchecked; unchecked or partial → checked.</summary>
    [RelayCommand]
    private void ToggleCheck(ExplorerRowViewModel? row)
    {
        if (_editor is not { } editor || Source.RootPath is not { } root)
        {
            return;
        }

        if (row is NodeRowViewModel { Node.Id: CustomFilesProjectSource.NodeId })
        {
            editor.SetCustomFilesVisible(editor.Current.HideCustomFiles);
            RefreshChecks();
            return;
        }

        string key;
        bool isFolder;
        switch (row)
        {
            case ProjectHeaderRowViewModel:
                key = string.Empty;
                isFolder = true;
                break;
            case NodeRowViewModel { Node.RelativePath: { } relative } node:
                key = relative;
                isFolder = node.IsFolder;
                break;
            default:
                return;
        }

        var check = editor.Evaluator.CheckStateOf(key, isFolder) != CheckState.Checked;
        if (isFolder)
        {
            var impact = editor.PreviewFolder(key, k => _context.Store.DirectoryExists(Path.Combine(root, k)));
            if (impact.ClearedDirectFiles > 0
                && !_context.Dialogs.Confirm("보이는 항목", $"직접 체크한 파일 {impact.ClearedDirectFiles}개도 해제됩니다. 계속할까요?"))
            {
                return;
            }

            editor.SetFolder(key, check);
        }
        else
        {
            editor.SetFile(key, check);
        }

        RefreshChecks();
    }

    private void RefreshChecks()
    {
        if (_editor is not { } editor)
        {
            return;
        }

        var evaluator = editor.Evaluator;
        Header.CheckState = evaluator.CheckStateOf(string.Empty, isDirectory: true);
        foreach (var node in BodyRows.OfType<NodeRowViewModel>())
        {
            node.CheckState = CheckStateOf(node.Node);
        }
    }

    [RelayCommand]
    private Task SetViewModeAsync(string? mode)
    {
        if (!SupportsVisibility || !Enum.TryParse<ExplorerViewMode>(mode, ignoreCase: true, out var next) || next == Project.View.Mode)
        {
            return Task.CompletedTask;
        }

        var id = Id;
        var view = Project.View with { Mode = next };
        return _owner.ChangeRegistryAsync(r => r.SetView(id, view));
    }

    [RelayCommand]
    private Task SetFlatSortAsync(string? sort)
    {
        if (!Enum.TryParse<FlatSort>(sort, ignoreCase: true, out var next) || next == Project.View.FlatSort)
        {
            return Task.CompletedTask;
        }

        var id = Id;
        var view = Project.View with { FlatSort = next };
        return _owner.ChangeRegistryAsync(r => r.SetView(id, view));
    }

    public bool IsPinned(NodeInfo node) => node.RelativePath is { Length: > 0 } key && Project.View.IsPinned(key);

    /// <summary>"최상위에 고정/해제": pinned items come first in every layout (SPEC 2.3).</summary>
    public Task TogglePinAsync(NodeInfo node)
    {
        if (!SupportsVisibility || node.RelativePath is not { Length: > 0 } key)
        {
            return Task.CompletedTask;
        }

        var pins = Project.View.IsPinned(key)
            ? Project.View.Pins.Where(p => !RelativeKey.Comparer.Equals(p, key)).ToList()
            : [.. Project.View.Pins, key];
        var id = Id;
        var view = Project.View with { Pins = pins };
        return _owner.ChangeRegistryAsync(r => r.SetView(id, view));
    }

    /// <summary>
    /// Flat view with the user's order: puts <paramref name="moved"/> before <paramref name="before"/> (null: at the end).
    /// Files not in the stored order keep their name order after it.
    /// </summary>
    public Task ReorderFlatAsync(IReadOnlyList<string> moved, string? before)
    {
        if (Source.RootPath is not { } root)
        {
            return Task.CompletedTask;
        }

        var movedKeys = moved.Select(p => RelativeKey.FromFullPath(root, p)).OfType<string>().Where(k => k.Length > 0).ToList();
        if (movedKeys.Count == 0)
        {
            return Task.CompletedTask;
        }

        // Start from what is shown now, so the drop lands where the user saw it.
        var shown = BodyRows.OfType<NodeRowViewModel>()
            .Where(r => r.Depth == 0 && !r.IsFolder && !r.Node.IsPinned && r.Node.RelativePath is not null)
            .Select(r => r.Node.RelativePath!)
            .ToList();
        var order = shown.Where(k => !movedKeys.Contains(k, RelativeKey.Comparer)).ToList();
        var beforeKey = before is null ? null : RelativeKey.FromFullPath(root, before);
        var index = beforeKey is null ? -1 : order.FindIndex(k => RelativeKey.Comparer.Equals(k, beforeKey));
        order.InsertRange(index < 0 ? order.Count : index, movedKeys);

        // Keep stored keys of files that are not shown right now (other folders hidden meanwhile) after the shown ones.
        order.AddRange(Project.View.FlatOrder.Where(k => !order.Contains(k, RelativeKey.Comparer)));
        var id = Id;
        var view = Project.View with { FlatSort = FlatSort.Custom, FlatOrder = order };
        return _owner.ChangeRegistryAsync(r => r.SetView(id, view));
    }

    /// <summary>"트리에서 위치 보기": the full tree of edit mode, with the item expanded to and selected.</summary>
    public async Task RevealInTreeAsync(NodeInfo node)
    {
        if (!SupportsVisibility || node.RelativePath is not { } key)
        {
            return;
        }

        for (var parent = RelativeKey.Parent(key); !string.IsNullOrEmpty(parent); parent = RelativeKey.Parent(parent))
        {
            _expanded.Add(parent);
        }

        _selectionOverride = key.Length == 0 ? null : key;
        if (_editor is null)
        {
            await ToggleVisibilityEditAsync();
        }
        else
        {
            await LoadAsync();
        }
    }

    private void RestoreSelection()
    {
        if (_pendingSelectionKey is not { } key)
        {
            return;
        }

        var row = BodyRows.OfType<NodeRowViewModel>().FirstOrDefault(r => ExplorerTreeBuilder.KeyComparer.Equals(r.Key, key));
        _pendingSelectionKey = null;
        if (row is null && FindChainRow(key) is { } found)
        {
            found.Row.ActiveSegment = found.Segment;
            row = found.Row;
        }

        if (row is not null)
        {
            List.RequestSelection(this, row);
        }
    }

    /// <summary>A merged compact row containing the folder <paramref name="key"/> (not as its last part).</summary>
    private (NodeRowViewModel Row, int Segment)? FindChainRow(string key)
    {
        foreach (var row in BodyRows.OfType<NodeRowViewModel>())
        {
            if (row.Node.Chain is { } chain)
            {
                var index = chain.ToList().FindIndex(n => n.RelativePath is { } k && ExplorerTreeBuilder.KeyComparer.Equals(k, key));
                if (index >= 0 && index < chain.Count - 1)
                {
                    return (row, index);
                }
            }
        }

        return null;
    }

    private void ReplaceBody(IReadOnlyList<ExplorerRowViewModel> body)
    {
        var (start, end) = Segment();
        if (start < 0)
        {
            return;
        }

        for (var i = end - 1; i >= start; i--)
        {
            Rows.RemoveAt(i);
        }

        for (var i = 0; i < body.Count; i++)
        {
            Rows.Insert(start + i, body[i]);
        }
    }

    /// <summary>[start, end) of the body rows in the shared list; start is -1 when the header is not in the list.</summary>
    private (int Start, int End) Segment()
    {
        var header = Rows.IndexOf(Header);
        if (header < 0)
        {
            return (-1, -1);
        }

        var end = header + 1;
        while (end < Rows.Count && Rows[end] is not ProjectHeaderRowViewModel)
        {
            end++;
        }

        return (header + 1, end);
    }

    private static bool SameList(IReadOnlyList<string>? a, IReadOnlyList<string>? b) =>
        a is null ? b is null : b is not null && a.SequenceEqual(b, StringComparer.Ordinal);
}
