using System.IO;
using DotDeck.App.ViewModels.Documents;
using DotDeck.Core.Editors;
using DotDeck.Core.Projects;
using DotDeck.Core.Roles;

namespace DotDeck.App.ViewModels;

/// <summary>File roles (SPEC 7.0): which editor a file gets, and the user's "다음 형식으로 열기" choices.</summary>
public sealed partial class ShellViewModel
{
    private readonly SemaphoreSlim _overrideWrites = new(1, 1);
    private IReadOnlyList<string> _projectRootSnapshot = [];

    // Roles resolved off the UI thread before a tab is created (resolution may check folders on slow drives).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, FileRole?> _roleCache = new(StringComparer.OrdinalIgnoreCase);

    public SettingsKeyCatalog? SettingsKeys { get; }

    /// <summary>The role of <paramref name="path"/> and the editor to use (the user's choice wins).</summary>
    /// <remarks>UI thread only when <paramref name="refreshRoots"/> (it reads the explorers).</remarks>
    private (FileRole? Role, EditorKind Kind, bool Overridden) EditorFor(string path, bool refreshRoots = true)
    {
        if (refreshRoots)
        {
            RefreshProjectRoots();
        }

        var role = _roleCache.TryGetValue(path, out var cached) ? cached : _roleCache[path] = _roles.Resolve(path);
        var chosen = _editorOverrides.Get(path);
        return (role, chosen ?? _roles.DefaultEditor(path, role), chosen is not null);
    }

    /// <summary>Resolves the role of <paramref name="path"/> on the thread pool (call on the UI thread before opening).</summary>
    private async Task PrepareRoleAsync(string path)
    {
        RefreshProjectRoots();
        var role = await Task.Run(() => _roles.Resolve(path)).ConfigureAwait(true);
        _roleCache[path] = role;
    }

    /// <summary>
    /// Folder project roots, for files outside .claude folders (CLAUDE.md, .mcp.json). The resolver may run on the
    /// thread pool, so it reads a snapshot taken on the UI thread (<see cref="RefreshProjectRoots"/>).
    /// </summary>
    private IReadOnlyList<string> ProjectRoots() => Volatile.Read(ref _projectRootSnapshot);

    /// <summary>Takes a new project root snapshot (UI thread).</summary>
    private void RefreshProjectRoots() =>
        Volatile.Write(ref _projectRootSnapshot, Projects is null
            ? []
            : Projects.Explorers
                .Where(e => e.Source.Project.Kind == ProjectKind.Folder && e.Source.RootPath is not null)
                .Select(e => e.Source.RootPath!)
                .ToList());

    public void SetEditorKind(TextDocumentViewModel document, EditorKind? kind)
    {
        ArgumentNullException.ThrowIfNull(document);
        _ = SetEditorKindAsync(document, kind);
    }

    private async Task SetEditorKindAsync(TextDocumentViewModel document, EditorKind? kind)
    {
        if (!await RememberEditorAsync(document.FilePath, kind).ConfigureAwait(true))
        {
            return;
        }

        ApplyEditorKind(document, kind);
    }

    /// <summary>Applies the stored choice for the document's file to all its tabs (UI thread).</summary>
    private void ApplyEditorKind(TextDocumentViewModel document, EditorKind? kind)
    {
        var (_, effective, overridden) = EditorFor(document.FilePath);
        foreach (var view in Documents.OfType<TextDocumentViewModel>()
                     .Where(d => string.Equals(d.FilePath, document.FilePath, StringComparison.OrdinalIgnoreCase)))
        {
            view.ApplyKind(effective, overridden);
        }

        if (kind is not null)
        {
            document.TryShowForm();
        }

        StatusMessage = kind is null
            ? $"자동 판별로 되돌렸습니다: {TextDocumentViewModel.KindLabel(effective)}"
            : $"{TextDocumentViewModel.KindLabel(effective)} 형식으로 엽니다.";
        RefreshValidationTarget();
        Properties.Refresh();
    }

    /// <summary>Explorer "다음 형식으로 열기": remembers the choice, opens the file and applies it.</summary>
    private async Task OpenFileAsAsync(string path, EditorKind? kind)
    {
        if (!await RememberEditorAsync(path, kind).ConfigureAwait(true))
        {
            return;
        }

        await OpenFileAsync(path, preview: false);
        if (Documents.OfType<TextDocumentViewModel>().FirstOrDefault(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase)) is { } document)
        {
            ApplyEditorKind(document, kind);
        }
    }

    /// <summary>Stores the choice off the UI thread; choices are written one at a time in request order.</summary>
    private async Task<bool> RememberEditorAsync(string path, EditorKind? kind)
    {
        await _overrideWrites.WaitAsync().ConfigureAwait(true);
        try
        {
            await Task.Run(() => _editorOverrides.Set(path, kind)).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReportError("열기 형식을 저장하지 못했습니다.", ex);
            return false;
        }
        finally
        {
            _overrideWrites.Release();
        }
    }

    /// <summary>Role lines for the properties pane, or null for files no catalog knows.</summary>
    private Tools.RoleDescription? DescribeRole(string path)
    {
        if (Directory.Exists(path))
        {
            return null;
        }

        // Runs on the thread pool: use the root snapshot the UI thread took before showing the item.
        var (role, kind, overridden) = EditorFor(path, refreshRoots: false);
        if (role is null && !overridden)
        {
            return null;
        }

        var lines = new List<string>();
        if (role is not null)
        {
            lines.Add($"역할: {role.RoleLabel}");
            lines.Add($"범위: {role.ScopeLabel}");
            lines.Add($"기준 루트: {role.BaseRoot}");
            lines.AddRange(role.Notes);
        }

        lines.Add($"형식: {TextDocumentViewModel.KindLabel(kind)}{(overridden ? " (수동 지정)" : string.Empty)}");
        var title = role is null ? Path.GetFileName(path) + " · 수동 지정" : role.Summary(path);
        return new Tools.RoleDescription(title, lines);
    }
}
