using System.IO;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotDeck.App.ViewModels.Dialogs;
using DotDeck.Core.Explorer;
using DotDeck.Core.Explorer.Visibility;
using DotDeck.Core.FileOperations;
using DotDeck.Core.Logging;

namespace DotDeck.App.ViewModels.Explorer;

/// <summary>One clickable part of the breadcrumb above the tree.</summary>
/// <param name="Target">The row to select, or null when that folder has no row of its own (hidden or merged).</param>
public sealed record BreadcrumbSegment(string Name, ExplorerRowViewModel? Target);

/// <summary>
/// A flattened, virtualized list of explorer sections with selection, breadcrumb and keyboard behavior.
/// Used by the project area (all attached projects) and by each detached explorer pane (one project).
/// </summary>
public sealed partial class ExplorerListViewModel : ObservableObject
{
    private readonly ExplorerContext _context;

    private IReadOnlyList<ExplorerRowViewModel> _selectedRows = [];

    public ExplorerListViewModel(ExplorerContext context)
    {
        _context = context;
    }

    /// <summary>All selected rows (multi-selection), in list order. Set by the view.</summary>
    public IReadOnlyList<ExplorerRowViewModel> SelectedRows => _selectedRows;

    public string UndoText => _context.FileOps.History.UndoDescription is { } d ? $"실행 취소: {d}" : "실행 취소";

    public string RedoText => _context.FileOps.History.RedoDescription is { } d ? $"다시 실행: {d}" : "다시 실행";

    /// <summary>Raised when a row should be scrolled into view (the view owns the ListBox).</summary>
    public event EventHandler<ExplorerRowViewModel>? ScrollIntoViewRequested;

    /// <summary>Raised when an in-place name editor should take keyboard focus (the view finds the box).</summary>
    public event EventHandler<ExplorerRowViewModel>? EditorFocusRequested;

    public void RequestEditorFocus(ExplorerRowViewModel row) => EditorFocusRequested?.Invoke(this, row);

    public ObservableCollection<ExplorerRowViewModel> Rows { get; } = [];

    public ObservableCollection<BreadcrumbSegment> Breadcrumb { get; } = [];

    [ObservableProperty]
    private ExplorerRowViewModel? _selectedRow;

    /// <summary>Full path of the selection, shown and copyable in the breadcrumb bar.</summary>
    [ObservableProperty]
    private string? _selectedPath;

    public void Select(ExplorerRowViewModel? row)
    {
        if (row is null || !Rows.Contains(row))
        {
            return;
        }

        foreach (var other in Rows)
        {
            if (other != row && other.IsSelected)
            {
                other.IsSelected = false;
            }
        }

        row.IsSelected = true;
        SelectedRow = row;
        ScrollIntoViewRequested?.Invoke(this, row);
    }

    /// <summary>
    /// An explorer asks to select a row (restored selection, or the folder of a collapsed selection).
    /// A live selection elsewhere is not stolen; an empty, removed or same-explorer selection is replaced.
    /// </summary>
    public void RequestSelection(ExplorerViewModel explorer, ExplorerRowViewModel row)
    {
        if (SelectedRow is null || !Rows.Contains(SelectedRow) || SelectedRow.Explorer == explorer)
        {
            Select(row);
        }
    }

    [RelayCommand]
    private async Task ActivateAsync(ExplorerRowViewModel? row)
    {
        switch (row)
        {
            case ProjectHeaderRowViewModel header:
                await header.Explorer.ToggleCollapsedCommand.ExecuteAsync(null);
                break;
            case NodeRowViewModel { IsFolder: true } folder:
                await folder.Explorer.ToggleNodeAsync(folder);
                break;
            case NodeRowViewModel { Node.IsSecret: true } secret:
                _context.SetStatus($"비밀 파일은 열 수 없습니다: {secret.Name}");
                break;
            case NodeRowViewModel { Node.CanOpen: true } file:
                await _context.OpenFile(file.Node.FullPath!, false);
                break;
            case NodeRowViewModel { Node.Kind: NodeKind.Virtual, Node.IsEnabled: true } system:
                _context.OpenSystemNode?.Invoke(system.Node.Id);
                break;
            case MessageRowViewModel { Action: { } action }:
                action.Execute(null);
                break;
        }
    }

    /// <summary>Single click on a file: show it in the reusable preview tab.</summary>
    [RelayCommand]
    private Task PreviewAsync(ExplorerRowViewModel? row) =>
        row is NodeRowViewModel { IsFolder: false, Node.IsSecret: false, Node.CanOpen: true } file
            ? _context.OpenFile(file.Node.FullPath!, true)
            : Task.CompletedTask;

    /// <summary>Right arrow: expand, or move to the first child when already expanded.</summary>
    [RelayCommand]
    private async Task ExpandOrDescendAsync(ExplorerRowViewModel? row)
    {
        switch (row)
        {
            case ProjectHeaderRowViewModel { IsCollapsed: true } header:
                await header.Explorer.SetCollapsedAsync(false);
                break;
            case ProjectHeaderRowViewModel header:
                Select(header.Explorer.BodyRows.FirstOrDefault());
                break;
            case NodeRowViewModel { IsFolder: true, IsExpanded: false } folder:
                await folder.Explorer.ExpandAsync(folder);
                break;
            case NodeRowViewModel { IsExpanded: true } folder:
                var next = Rows.IndexOf(folder) + 1;
                if (next < Rows.Count && Rows[next] is NodeRowViewModel child && child.Depth > folder.Depth)
                {
                    Select(child);
                }

                break;
        }
    }

    /// <summary>Left arrow: collapse, or move to the parent.</summary>
    [RelayCommand]
    private async Task CollapseOrAscendAsync(ExplorerRowViewModel? row)
    {
        switch (row)
        {
            case ProjectHeaderRowViewModel { IsCollapsed: false } header:
                await header.Explorer.SetCollapsedAsync(true);
                break;
            case NodeRowViewModel { IsExpanded: true } folder:
                folder.Explorer.Collapse(folder);
                break;
            case NodeRowViewModel node:
                Select(FindParent(node));
                break;
            case MessageRowViewModel message:
                Select(message.Explorer.Header);
                break;
        }
    }

    [RelayCommand]
    private void SelectBreadcrumb(BreadcrumbSegment? segment) => Select(segment?.Target);

    /// <summary>Click on one folder of a merged compact row: that folder becomes the target of commands.</summary>
    [RelayCommand]
    private void SelectSegment(ChainSegment? segment)
    {
        if (segment is null)
        {
            return;
        }

        segment.Row.ActiveSegment = segment.IsLast ? null : segment.Index;
        Select(segment.Row);
        UpdateBreadcrumb(segment.Row);
        RefreshCommandStates();
    }

    [RelayCommand]
    private void CopySelectedPath()
    {
        if (SelectedPath is { } path)
        {
            _context.SetStatus(_context.Clipboard.TrySetText(path) ? $"경로를 복사했습니다: {path}" : "클립보드를 사용할 수 없습니다.");
        }
    }

    // ===== Selection =====

    /// <summary>Called by the view whenever the ListBox selection changes (it knows virtualized selections too).</summary>
    public void UpdateSelection(IEnumerable<ExplorerRowViewModel> rows)
    {
        _selectedRows = [.. rows];
    }

    /// <summary>File system rows the selection-based commands act on (selected, else the focused row).</summary>
    private List<NodeRowViewModel> TargetNodes()
    {
        var rows = _selectedRows.Count > 0 ? _selectedRows : SelectedRow is { } single ? [single] : [];
        return [.. rows.OfType<NodeRowViewModel>().Where(r => r.IsFileSystemItem)];
    }

    private static List<string> PathsOf(IEnumerable<NodeRowViewModel> rows) => [.. rows.Select(r => r.TargetNode.FullPath!)];

    /// <summary>Folder that a paste or drop onto <paramref name="row"/> goes into, or null if it cannot take items.</summary>
    public static string? TargetFolderOf(ExplorerRowViewModel? row) => row switch
    {
        NodeRowViewModel { IsFileSystemItem: true, IsFolder: true } folder => folder.TargetNode.FullPath,
        NodeRowViewModel { IsFileSystemItem: true } file => Path.GetDirectoryName(file.Node.FullPath),
        ProjectHeaderRowViewModel or MessageRowViewModel or NewItemRowViewModel => row.Explorer.Source.RootPath,
        _ => null,
    };

    /// <summary>Re-evaluates menu item states (called when the context menu opens).</summary>
    public void RefreshCommandStates()
    {
        OnPropertyChanged(nameof(UndoText));
        OnPropertyChanged(nameof(RedoText));
        OnPropertyChanged(nameof(PinText));
        OnPropertyChanged(nameof(IsVisibilityTarget));
        foreach (var command in new IRelayCommand[]
                 {
                     OpenSelectedCommand, NewFileCommand, NewFolderCommand, RenameCommand, DeleteCommand, CutCommand, CopyCommand,
                     PasteCommand, DuplicateCommand, UndoCommand, RedoCommand, CopyFullPathsCommand, CopyRelativePathsCommand,
                     RevealSelectedCommand, OpenInVsCodeCommand, OpenTerminalCommand, OpenAsCommand, TogglePinCommand, RevealInTreeCommand,
                 })
        {
            command.NotifyCanExecuteChanged();
        }
    }

    // ===== File operations (SPEC 2.3) =====

    private bool HasTargets() => TargetNodes().Count > 0;

    private bool HasSingleTarget() => TargetNodes().Count == 1;

    private bool HasTargetFolder() => TargetFolderOf(SelectedRow) is not null;

    [RelayCommand(CanExecute = nameof(HasSingleTarget))]
    private Task OpenSelectedAsync() => ActivateAsync(TargetNodes().FirstOrDefault());

    private bool HasSingleFile() => TargetNodes() is [{ IsFolder: false }] && _context.OpenAs is not null;

    /// <summary>"다음 형식으로 열기": text, markdown, settingsForm, agentForm, skillForm, or auto (automatic detection).</summary>
    [RelayCommand(CanExecute = nameof(HasSingleFile))]
    private Task OpenAsAsync(string? name) =>
        TargetNodes() is [{ IsFolder: false } file] && _context.OpenAs is { } openAs
            ? openAs(file.Node.FullPath!, name is null or "auto" ? null : DotDeck.Core.Roles.EditorOverrideStore.NameToKind(name))
            : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(HasTargetFolder))]
    private Task NewFileAsync() => BeginNewItemAsync(isFolder: false);

    [RelayCommand(CanExecute = nameof(HasTargetFolder))]
    private Task NewFolderAsync() => BeginNewItemAsync(isFolder: true);

    private async Task BeginNewItemAsync(bool isFolder)
    {
        var row = SelectedRow;
        if (row is null || TargetFolderOf(row) is not { } directory)
        {
            return;
        }

        switch (row)
        {
            case NodeRowViewModel { IsFileSystemItem: true, IsFolder: true } folder when folder.TargetNode == folder.Node:
                await row.Explorer.BeginNewItemAsync(folder, isFolder);
                break;
            case NodeRowViewModel { IsFileSystemItem: true } item:
                // A file (or a folder inside a merged row): its folder may have no row of its own (flat view,
                // pinned item), so the placeholder goes right after the selected row.
                var parent = FindParent(item) as NodeRowViewModel;
                if (parent is not null && string.Equals(parent.TargetNode.FullPath, directory, StringComparison.OrdinalIgnoreCase))
                {
                    await row.Explorer.BeginNewItemAsync(parent, isFolder);
                }
                else if (item.Explorer.Source.RootPath is { } root && string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(directory), StringComparison.OrdinalIgnoreCase)
                         && item.Depth == 0 && !item.Node.IsPinned)
                {
                    await row.Explorer.BeginNewItemAsync(null, isFolder);
                }
                else
                {
                    await row.Explorer.BeginNewItemAsync(null, isFolder, directory, sibling: item);
                }

                break;
            default:
                await row.Explorer.BeginNewItemAsync(null, isFolder);
                break;
        }
    }

    /// <summary>F2: edit the name in place.</summary>
    [RelayCommand(CanExecute = nameof(HasSingleTarget))]
    private void Rename()
    {
        if (TargetNodes() is [var row])
        {
            BeginRename(row);
        }
    }

    private void BeginRename(NodeRowViewModel row)
    {
        row.EditText = row.TargetNode.Name;
        row.IsEditing = true;
        RequestEditorFocus(row);
    }

    /// <summary>Enter (or focus leaving the box) in an inline name editor.</summary>
    public async Task CommitEditAsync(IEditableRow row)
    {
        if (!row.IsEditing)
        {
            return; // already committed (Enter, then focus loss) or cancelled
        }

        row.IsEditing = false;
        var name = row.EditText;
        switch (row)
        {
            case NodeRowViewModel node:
                if (string.IsNullOrEmpty(name) || string.Equals(name, node.TargetNode.Name, StringComparison.Ordinal))
                {
                    return;
                }

                var path = node.TargetNode.FullPath!;
                var renamed = await _context.FileOps.RunAsync(s => s.Rename(path, name), [path]);
                if (renamed is null)
                {
                    // Keep the typed name so the user can correct it.
                    node.IsEditing = true;
                    RequestEditorFocus(node);
                }

                break;

            case NewItemRowViewModel newItem:
                if (string.IsNullOrWhiteSpace(name))
                {
                    CancelNewItem(newItem);
                    return;
                }

                var directory = newItem.ParentDirectory;
                var created = await _context.FileOps.RunAsync(
                    s => newItem.IsFolder ? s.CreateFolder(directory, name) : s.CreateFile(directory, name),
                    [directory]);
                if (created is null)
                {
                    newItem.IsEditing = true;
                    RequestEditorFocus(newItem);
                }
                else
                {
                    newItem.Explorer.RemoveRow(newItem);
                }

                break;
        }
    }

    /// <summary>Esc in an inline name editor.</summary>
    public void CancelEdit(IEditableRow row)
    {
        row.IsEditing = false;
        if (row is NewItemRowViewModel newItem)
        {
            CancelNewItem(newItem);
        }
    }

    private void CancelNewItem(NewItemRowViewModel row)
    {
        var explorer = row.Explorer;
        explorer.RemoveRow(row);
        Select(explorer.Header);

        // The placeholder replaced an "(비어 있음)" message: bring it back.
        if (!explorer.BodyRows.Any())
        {
            _ = explorer.LoadAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(HasTargets))]
    private async Task DeleteAsync()
    {
        var targets = TargetNodes();
        if (targets.Count == 0)
        {
            return;
        }

        var paths = PathsOf(targets);
        if (!await Task.Run(() => _context.FileOps.Service.CanRecycleAll(paths)))
        {
            _context.Dialogs.ShowError("삭제", "휴지통을 쓸 수 없는 위치(네트워크·이동식 드라이브)가 포함되어 있습니다.\nDotDeck은 영구 삭제를 하지 않습니다.");
            return;
        }

        var names = string.Join("\n", targets.Take(5).Select(t => $"  {t.Name}"));
        var more = targets.Count > 5 ? $"\n  … 외 {targets.Count - 5}개" : string.Empty;
        if (!_context.Dialogs.Confirm("삭제", $"{targets.Count}개 항목을 휴지통으로 보낼까요?\n\n{names}{more}\n\n실행 취소(Ctrl+Z)로 되돌릴 수 있습니다."))
        {
            return;
        }

        await _context.FileOps.RunAsync(s => s.Delete(paths), paths);
    }

    [RelayCommand(CanExecute = nameof(HasTargets))]
    private void Cut() => PutOnClipboard(cut: true);

    [RelayCommand(CanExecute = nameof(HasTargets))]
    private void Copy() => PutOnClipboard(cut: false);

    private void PutOnClipboard(bool cut)
    {
        var paths = PathsOf(TargetNodes());
        if (paths.Count == 0)
        {
            return;
        }

        _context.SetStatus(_context.Clipboard.TrySetFiles(paths, cut)
            ? $"{paths.Count}개 항목을 {(cut ? "잘라냈습니다" : "복사했습니다")}."
            : "클립보드를 사용할 수 없습니다.");
    }

    /// <summary>What the focused row means as a paste target (SPEC 2.3 paste rules).</summary>
    private static PasteSelection SelectionOf(ExplorerRowViewModel? row) => row switch
    {
        NodeRowViewModel { IsFileSystemItem: true, IsFolder: true } folder => new PasteSelection(
            PasteSelectionKind.Folder,
            folder.Node.FullPath,
            folder.Node.Chain?.Select(n => n.FullPath!).ToList(),
            folder.ActiveSegment),
        NodeRowViewModel { IsFileSystemItem: true } file => new PasteSelection(PasteSelectionKind.File, file.Node.FullPath),
        ProjectHeaderRowViewModel or MessageRowViewModel or NewItemRowViewModel when row.Explorer.Source.RootPath is { } root =>
            new PasteSelection(PasteSelectionKind.ProjectRoot, root),
        _ => new PasteSelection(PasteSelectionKind.None, null),
    };

    /// <summary>Ctrl+V: into the selected folder, next to the selected file; conflicts are asked one by one.</summary>
    [RelayCommand]
    private async Task PasteAsync()
    {
        string target;
        try
        {
            target = PasteTargetResolver.Resolve(SelectionOf(SelectedRow));
        }
        catch (FileOperationException ex)
        {
            _context.SetStatus(ex.Message);
            _context.Dialogs.ShowError("붙여넣기", ex.Message);
            return;
        }

        if (_context.Clipboard.TryGetFiles() is not { } files)
        {
            _context.SetStatus("붙여넣을 파일이 클립보드에 없습니다.");
            return;
        }

        var paths = files.Paths;
        var service = _context.FileOps.Service;
        PastePlan plan;
        try
        {
            plan = await Task.Run(() => service.PlanPaste(paths, target, files.IsCut));
        }
        catch (FileOperationException ex)
        {
            _context.SetStatus(ex.Message.Split('\n')[0]);
            _context.Dialogs.ShowError("붙여넣기", ex.Message);
            return;
        }

        if (AskConflicts(plan) is not { } choices)
        {
            _context.SetStatus("붙여넣기를 취소했습니다.");
            return;
        }

        var result = await _context.FileOps.RunAsync(s => s.Paste(plan, choices), [.. paths, target]);
        if (result is not null && files.IsCut)
        {
            // Like Explorer: cut items are moved once, then the clipboard no longer offers them.
            _context.Clipboard.ClearCutFiles(paths);
        }
    }

    /// <summary>"덮어쓰기 / 둘 다 유지 / 건너뛰기" for each conflict ("모든 항목에 적용" answers the rest). Null: cancelled.</summary>
    private Dictionary<string, ConflictChoice>? AskConflicts(PastePlan plan)
    {
        var choices = new Dictionary<string, ConflictChoice>(StringComparer.OrdinalIgnoreCase);
        ConflictChoice? forAll = null;
        var conflicts = plan.Conflicts.ToList();
        for (var i = 0; i < conflicts.Count; i++)
        {
            var item = conflicts[i];
            if (forAll is { } all && (all != ConflictChoice.Overwrite || item.CanOverwrite))
            {
                choices[item.Source] = all;
                continue;
            }

            List<DialogChoice> buttons = [];
            if (item.CanOverwrite)
            {
                buttons.Add(new DialogChoice("overwrite", "덮어쓰기"));
            }

            buttons.Add(new DialogChoice("keep", "둘 다 유지", IsDefault: true));
            buttons.Add(new DialogChoice("skip", "건너뛰기"));
            buttons.Add(new DialogChoice("cancel", "취소", IsCancel: true));
            var name = Path.GetFileName(item.Destination);
            var message = item.CanOverwrite
                ? $"'{name}' 파일이 이미 있습니다.\n덮어쓰면 기존 파일은 백업한 뒤 휴지통으로 보냅니다."
                : $"'{name}' 폴더가 이미 있습니다. 폴더는 덮어쓸 수 없습니다.";
            var (choice, applyToAll) = _context.Dialogs.AskChoice(
                $"붙여넣기 — 이름 충돌 ({i + 1}/{conflicts.Count})",
                message,
                buttons,
                conflicts.Count - i > 1 ? "남은 충돌 모두에 적용" : null);
            ConflictChoice? picked = choice switch
            {
                "overwrite" => ConflictChoice.Overwrite,
                "keep" => ConflictChoice.KeepBoth,
                "skip" => ConflictChoice.Skip,
                _ => null,
            };
            if (picked is not { } value)
            {
                return null;
            }

            choices[item.Source] = value;
            if (applyToAll)
            {
                forAll = value;
            }
        }

        return choices;
    }

    /// <summary>Ctrl+D: a copy next to each item ("이름 - 복사본"); a single copy is selected for renaming (F2).</summary>
    [RelayCommand(CanExecute = nameof(HasTargets))]
    private async Task DuplicateAsync()
    {
        var paths = PathsOf(TargetNodes());
        var result = await _context.FileOps.RunAsync(s => s.Duplicate(paths), paths);
        if (result?.Paths is [var copy]
            && Rows.OfType<NodeRowViewModel>().FirstOrDefault(r => string.Equals(r.Node.FullPath, copy, StringComparison.OrdinalIgnoreCase)) is { } row)
        {
            Select(row);
            BeginRename(row);
        }
    }

    /// <summary>
    /// Flat view sorted by the user's order: dragging files of the same project onto a file reorders them
    /// instead of moving them (SPEC 2.3). Returns the explorer when that applies.
    /// </summary>
    public static ExplorerViewModel? FlatReorderTarget(IReadOnlyList<string> paths, ExplorerRowViewModel? row) =>
        row is NodeRowViewModel { IsFolder: false, Depth: 0, Node.IsPinned: false } file
        && file.Explorer is { IsFlatMode: true, IsCustomFlatSort: true, IsEditingVisibility: false } explorer
        && paths.Count > 0
        && paths.All(p => explorer.BodyRows.OfType<NodeRowViewModel>().Any(r => r is { IsFolder: false, Depth: 0 } && string.Equals(r.Node.FullPath, p, StringComparison.OrdinalIgnoreCase)))
            ? explorer
            : null;

    public Task ReorderFlatAsync(ExplorerViewModel explorer, IReadOnlyList<string> paths, NodeRowViewModel before) =>
        explorer.ReorderFlatAsync(paths, before.Node.FullPath);

    /// <summary>Drop from a drag (inside DotDeck or from Windows Explorer) into <paramref name="targetFolder"/>.</summary>
    public async Task DropAsync(IReadOnlyList<string> paths, string targetFolder, bool copy)
    {
        if (paths.Count == 0)
        {
            return;
        }

        await _context.FileOps.RunAsync(s => copy ? s.Copy(paths, targetFolder) : s.Move(paths, targetFolder), [.. paths, targetFolder]);
    }

    /// <summary>True when dropping <paramref name="paths"/> into <paramref name="targetFolder"/> would do something sensible.</summary>
    public static bool CanDrop(IReadOnlyList<string> paths, string targetFolder, bool copy)
    {
        var target = Path.TrimEndingDirectorySeparator(targetFolder);
        foreach (var path in paths)
        {
            var source = Path.TrimEndingDirectorySeparator(path);
            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)
                || target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return false; // into itself
            }

            if (!copy && string.Equals(Path.GetDirectoryName(source), target, StringComparison.OrdinalIgnoreCase))
            {
                return false; // already there
            }
        }

        return true;
    }

    private bool CanUndo() => _context.FileOps.History.CanUndo;

    private bool CanRedo() => _context.FileOps.History.CanRedo;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private Task UndoAsync() => _context.FileOps.UndoAsync();

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private Task RedoAsync() => _context.FileOps.RedoAsync();

    [RelayCommand(CanExecute = nameof(HasTargets))]
    private void CopyFullPaths() => CopyPathsText(TargetNodes().Select(r => r.Node.FullPath!));

    [RelayCommand(CanExecute = nameof(HasTargets))]
    private void CopyRelativePaths() => CopyPathsText(TargetNodes().Select(r => r.Node.RelativePath ?? r.Name));

    private void CopyPathsText(IEnumerable<string> paths)
    {
        var text = string.Join(Environment.NewLine, paths);
        if (text.Length > 0)
        {
            _context.SetStatus(_context.Clipboard.TrySetText(text) ? $"경로를 복사했습니다: {text.Split(Environment.NewLine)[0]}" : "클립보드를 사용할 수 없습니다.");
        }
    }

    [RelayCommand(CanExecute = nameof(HasSingleTarget))]
    private void RevealSelected()
    {
        if (TargetNodes() is [var row])
        {
            row.Explorer.Reveal(row.Node.FullPath!);
        }
    }

    [RelayCommand(CanExecute = nameof(HasTargetFolder))]
    private void OpenInVsCode()
    {
        // Secret files are never opened (SPEC 2.3), not even in another editor.
        if (TargetNodes() is [{ Node.IsSecret: true } secret])
        {
            _context.SetStatus($"비밀 파일은 열 수 없습니다: {secret.Name}");
            return;
        }

        // A selected file opens itself; otherwise the folder.
        var path = TargetNodes() is [{ IsFolder: false } file] ? file.Node.FullPath : TargetFolderOf(SelectedRow);
        if (path is null)
        {
            return;
        }

        try
        {
            if (!_context.Shell.OpenInVsCode(path))
            {
                _context.SetStatus("VS Code를 찾지 못했습니다.");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _context.Log.Error($"VS Code를 열지 못했습니다: {path}", ex);
            _context.SetStatus($"VS Code를 열지 못했습니다: {ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(HasTargetFolder))]
    private void OpenTerminal()
    {
        if (TargetFolderOf(SelectedRow) is not { } folder)
        {
            return;
        }

        try
        {
            _context.Shell.OpenTerminal(folder);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _context.Log.Error($"터미널을 열지 못했습니다: {folder}", ex);
            _context.SetStatus($"터미널을 열지 못했습니다: {ex.Message}");
        }
    }

    partial void OnSelectedRowChanged(ExplorerRowViewModel? oldValue, ExplorerRowViewModel? newValue)
    {
        if (oldValue is NodeRowViewModel { ActiveSegment: not null } old && old != newValue)
        {
            old.ActiveSegment = null;
        }
    }

    /// <summary>A file system item of a folder project (pin, "트리에서 위치 보기").</summary>
    public bool IsVisibilityTarget => SelectedRow is NodeRowViewModel { IsFileSystemItem: true, Explorer.SupportsVisibility: true };

    private bool CanPin() => IsVisibilityTarget;

    public string PinText => SelectedRow is NodeRowViewModel { IsFileSystemItem: true } row && row.Explorer.IsPinned(row.TargetNode)
        ? "고정 해제(_N)"
        : "최상위에 고정(_N)";

    [RelayCommand(CanExecute = nameof(CanPin))]
    private Task TogglePinAsync() =>
        SelectedRow is NodeRowViewModel { IsFileSystemItem: true } row ? row.Explorer.TogglePinAsync(row.TargetNode) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanPin))]
    private Task RevealInTreeAsync() =>
        SelectedRow is NodeRowViewModel { IsFileSystemItem: true } row ? row.Explorer.RevealInTreeAsync(row.TargetNode) : Task.CompletedTask;

    partial void OnSelectedRowChanged(ExplorerRowViewModel? value)
    {
        UpdateBreadcrumb(value);
        RefreshCommandStates();
        OnPropertyChanged(nameof(IsOtherFilesTarget));
        OnPropertyChanged(nameof(IsOtherFileItem));
    }

    /// <summary>The System project's "other files" node or one of its files is selected (context menu items).</summary>
    public bool IsOtherFilesTarget => SelectedRow is NodeRowViewModel { Explorer.Source: SystemProjectSource } row
        && (row.Node.Id == SystemProjectSource.ImportantFilesId || row.Node.Kind == NodeKind.File);

    public bool IsOtherFileItem => SelectedRow is NodeRowViewModel { Explorer.Source: SystemProjectSource, Node.Kind: NodeKind.File };

    [RelayCommand]
    private Task RegisterOtherFileAsync() => _context.RegisterOtherFile?.Invoke() ?? Task.CompletedTask;

    [RelayCommand]
    private Task UnregisterOtherFileAsync() =>
        IsOtherFileItem && SelectedRow is NodeRowViewModel { Node.FullPath: { } path } && _context.UnregisterOtherFile is { } remove
            ? remove(path)
            : Task.CompletedTask;

    private void UpdateBreadcrumb(ExplorerRowViewModel? row)
    {
        Breadcrumb.Clear();
        SelectedPath = null;
        if (row is null)
        {
            return;
        }

        var explorer = row.Explorer;
        Breadcrumb.Add(new BreadcrumbSegment(explorer.Header.Name, explorer.Header));
        if (row is not NodeRowViewModel node)
        {
            SelectedPath = explorer.Source.RootPath;
            return;
        }

        // Built from the path, not the row layout: merged, flat and pinned rows show their full path too.
        var target = node.TargetNode;
        if (target.RelativePath is { Length: > 0 } relative)
        {
            var parts = relative.Split(RelativeKey.Separator);
            for (var i = 0; i < parts.Length; i++)
            {
                var key = string.Join(RelativeKey.Separator, parts.Take(i + 1));
                var rowOfKey = i == parts.Length - 1
                    ? node
                    : explorer.BodyRows.OfType<NodeRowViewModel>().FirstOrDefault(r => ExplorerTreeBuilder.KeyComparer.Equals(r.Key, key));
                Breadcrumb.Add(new BreadcrumbSegment(parts[i], rowOfKey));
            }
        }
        else
        {
            Breadcrumb.Add(new BreadcrumbSegment(node.Name, node));
        }

        SelectedPath = target.FullPath ?? node.Name;
    }

    /// <summary>The nearest row above with a smaller depth, or the project header.</summary>
    private ExplorerRowViewModel? FindParent(NodeRowViewModel node)
    {
        var index = Rows.IndexOf(node);
        for (var i = index - 1; i >= 0; i--)
        {
            switch (Rows[i])
            {
                case ProjectHeaderRowViewModel header:
                    return header;
                case NodeRowViewModel candidate when candidate.Depth < node.Depth:
                    return candidate;
            }
        }

        return null;
    }
}
