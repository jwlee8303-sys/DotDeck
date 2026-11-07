using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DotDeck.Core.Files;
using DotDeck.Core.Logging;

namespace DotDeck.Core.Projects;

public enum ProjectRegistryLoadStatus
{
    /// <summary>projects.json was read successfully.</summary>
    Loaded,

    /// <summary>No file existed; defaults were created and saved.</summary>
    CreatedDefaults,

    /// <summary>The file was unreadable; it was preserved as <c>.corrupt-*</c> and defaults were created.</summary>
    RecoveredFromCorrupt,

    /// <summary>The file comes from a newer version; projects are shown but changes are not saved.</summary>
    ReadOnlyNewerSchema,

    /// <summary>The file exists but could not be read or preserved; it is left untouched and changes are not saved.</summary>
    ReadOnlyUnreadable,
}

public sealed record ProjectRegistryLoadResult(ProjectRegistryLoadStatus Status, string? Message = null);

/// <summary>Another DotDeck instance changed projects.json; the registry has reloaded it. Retry the change.</summary>
public sealed class ProjectsConflictException : Exception
{
    public ProjectsConflictException()
        : base("다른 DotDeck 창에서 프로젝트 목록이 변경되어 다시 불러왔습니다. 다시 시도하세요.")
    {
    }
}

/// <summary>
/// Registered projects (projects.json). Built-in projects always exist and cannot be removed or renamed.
/// Every change is validated, saved atomically, and only then becomes visible. Removing a project never
/// touches files on disk (SPEC 2.2).
/// </summary>
public sealed partial class ProjectRegistry
{
    public const int MaxNameLength = 100;

    private readonly IFileStore _store;
    private readonly string _filePath;
    private readonly TimeProvider _time;
    private readonly IAppLog _log;

    private static readonly TimeSpan s_lockTimeout = TimeSpan.FromSeconds(2);

    private List<ProjectDefinition> _projects = [];
    private IReadOnlyList<JsonNode> _unrecognized = [];
    private JsonObject? _rootExtra;

    // Text of projects.json as last read or written by this instance (null = file did not exist).
    // A different text on disk at save time means another DotDeck instance changed it.
    private string? _lastKnownText;

    public ProjectRegistry(IFileStore store, string filePath, TimeProvider time, IAppLog log)
    {
        _store = store;
        _filePath = filePath;
        _time = time;
        _log = log;
    }

    public event EventHandler? Changed;

    /// <summary>Projects ordered for display (including hidden ones).</summary>
    public IReadOnlyList<ProjectDefinition> Projects => _projects;

    public bool IsReadOnly { get; private set; }

    public string FilePath => _filePath;

    public ProjectDefinition? Find(string id) => _projects.FirstOrDefault(p => p.Id == id);

    public ProjectRegistryLoadResult Load()
    {
        IsReadOnly = false;
        _lastKnownText = null;
        try
        {
            // The whole read → (recover) → write sequence runs under the cross-process lock, so another
            // instance can never save in between (e.g. after we judged the file corrupt but before moving it).
            using (_store.AcquireLock(LockPath, s_lockTimeout))
            {
                return LoadLocked();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file may be fine but temporarily locked or inaccessible, or the folder is not writable:
            // never touch it, continue read-only.
            ResetToDefaults();
            IsReadOnly = true;
            _lastKnownText = null;
            _log.Error($"프로젝트 목록을 읽거나 저장할 수 없어 읽기 전용으로 시작합니다: {_filePath}", ex);
            return new ProjectRegistryLoadResult(
                ProjectRegistryLoadStatus.ReadOnlyUnreadable,
                $"프로젝트 목록 파일을 읽거나 저장할 수 없습니다({ex.Message}). 기본 프로젝트만 표시하며 변경 사항은 저장하지 않습니다.");
        }
    }

    private string LockPath => _filePath + ".lock";

    private ProjectRegistryLoadResult LoadLocked()
    {
        if (!_store.FileExists(_filePath))
        {
            ResetToDefaults();
            WriteLocked(ProjectsFileSerializer.Serialize(new ProjectsFileContent(_projects, _unrecognized, _rootExtra)));
            _log.Info($"프로젝트 목록을 새로 만들었습니다: {_filePath}");
            return new ProjectRegistryLoadResult(ProjectRegistryLoadStatus.CreatedDefaults);
        }

        try
        {
            string text;
            try
            {
                text = _store.ReadAllText(_filePath);
            }
            catch (DecoderFallbackException ex)
            {
                throw new ProjectsFileException("projects.json is not valid UTF-8.", ex);
            }

            var content = ProjectsFileSerializer.Parse(text);
            _lastKnownText = text;
            _projects = Normalize(EnsureBuiltins(content.Projects));
            _unrecognized = content.UnrecognizedEntries;
            _rootExtra = content.RootExtra;
            if (_unrecognized.Count > 0)
            {
                _log.Warning($"projects.json에서 해석할 수 없는 항목 {_unrecognized.Count}개는 그대로 보존합니다.");
            }

            return new ProjectRegistryLoadResult(ProjectRegistryLoadStatus.Loaded);
        }
        catch (ProjectsFileException ex) when (ex.IsNewerSchema)
        {
            ResetToDefaults();
            IsReadOnly = true;
            _log.Warning(ex.Message);
            return new ProjectRegistryLoadResult(
                ProjectRegistryLoadStatus.ReadOnlyNewerSchema,
                "프로젝트 목록이 더 새로운 DotDeck 버전에서 저장되었습니다. 기본 프로젝트만 표시하며 변경 사항은 저장하지 않습니다.");
        }
        catch (ProjectsFileException ex)
        {
            // Only a file that was read but could not be parsed counts as corrupt. If it cannot be moved
            // aside, never overwrite it: continue read-only instead.
            var preserved = PreserveCorruptFile();
            ResetToDefaults();
            if (preserved is null)
            {
                IsReadOnly = true;
                _log.Error($"손상된 프로젝트 목록을 보존하지 못해 읽기 전용으로 시작합니다: {_filePath}", ex);
                return new ProjectRegistryLoadResult(
                    ProjectRegistryLoadStatus.ReadOnlyUnreadable,
                    $"프로젝트 목록 파일이 손상되었고 백업하지 못했습니다. 기본 프로젝트만 표시하며 변경 사항은 저장하지 않습니다.\n{_filePath}");
            }

            // Write failures propagate to Load(), which switches to read-only.
            WriteLocked(ProjectsFileSerializer.Serialize(new ProjectsFileContent(_projects, _unrecognized, _rootExtra)));
            _log.Warning($"프로젝트 목록이 손상되어 기본값으로 다시 만들었습니다. 원본: {preserved}", ex);
            return new ProjectRegistryLoadResult(
                ProjectRegistryLoadStatus.RecoveredFromCorrupt,
                $"프로젝트 목록 파일이 손상되어 기본값으로 다시 만들었습니다.\n원본 보존 위치: {preserved}");
        }
    }

    public ProjectDefinition AddFolder(string name, string root, string? color = null, ExplorerFilterMode filter = ExplorerFilterMode.ConfigDocs)
    {
        var validName = ValidateName(name, exceptId: null);
        var validRoot = ValidateRoot(root, exceptId: null);
        var project = new ProjectDefinition
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = ProjectKind.Folder,
            Name = validName,
            Root = validRoot,
            Color = ValidateColor(color),
            Filter = filter,
            Order = _projects.Count,
        };

        Commit([.. _projects, project]);
        _log.Info($"프로젝트 추가: {validName} → {validRoot}");
        return project;
    }

    /// <summary>Removes a user project from the list. Files on disk are not touched.</summary>
    public void Remove(string id)
    {
        var project = Require(id);
        if (project.IsBuiltin)
        {
            throw new InvalidOperationException("기본 제공 프로젝트는 제거할 수 없습니다. 숨기기를 사용하세요.");
        }

        // Also drop preserved duplicates with the same id, otherwise the project would reappear on next load.
        var remainingRaw = _unrecognized
            .Where(n => !(n is JsonObject o && o["id"] is JsonValue v && v.TryGetValue<string>(out var rawId) && rawId == id))
            .ToList();
        Commit(_projects.Where(p => p.Id != id).ToList(), remainingRaw);
        _log.Info($"프로젝트 제거(목록에서만): {project.Name}");
    }

    public void Rename(string id, string name)
    {
        var project = RequireFolder(id, "기본 제공 프로젝트의 이름은 바꿀 수 없습니다.");
        var validName = ValidateName(name, exceptId: id);
        Replace(project with { Name = validName });
    }

    public void ChangeRoot(string id, string root)
    {
        var project = RequireFolder(id, "기본 제공 프로젝트의 루트는 바꿀 수 없습니다.");
        var validRoot = ValidateRoot(root, exceptId: id);
        Replace(project with { Root = validRoot });
    }

    /// <summary>
    /// Changes the editable settings in one validated commit (nothing is saved if any value is invalid).
    /// Built-in projects accept only color and filter; their name and root must be passed unchanged or null.
    /// </summary>
    public ProjectDefinition Update(string id, string? name, string? root, string? color, ExplorerFilterMode filter)
    {
        var project = Require(id);
        var updated = project with { Color = ValidateColor(color), Filter = filter };
        if (project.IsBuiltin)
        {
            if ((name is not null && name != project.Name) || root is not null)
            {
                throw new InvalidOperationException("기본 제공 프로젝트의 이름과 루트는 바꿀 수 없습니다.");
            }
        }
        else
        {
            updated = updated with
            {
                Name = name is null ? project.Name : ValidateName(name, exceptId: id),
                Root = root is null ? project.Root : ValidateRoot(root, exceptId: id),
            };
        }

        Replace(updated);
        return updated;
    }

    public void SetHidden(string id, bool hidden) => Replace(Require(id) with { Hidden = hidden });

    public void SetFilter(string id, ExplorerFilterMode filter) => Replace(Require(id) with { Filter = filter });

    public void SetColor(string id, string? color) => Replace(Require(id) with { Color = ValidateColor(color) });

    /// <summary>Null restores the defaults.</summary>
    public void SetExcludedFolders(string id, IReadOnlyList<string>? folders)
    {
        var cleaned = folders?
            .Select(f => f.Trim())
            .Where(f => f.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Replace(Require(id) with { ExcludedFolders = cleaned });
    }

    /// <summary>Moves a project to a new display index (clamped).</summary>
    public void MoveTo(string id, int newIndex)
    {
        var project = Require(id);
        var list = _projects.Where(p => p.Id != id).ToList();
        list.Insert(Math.Clamp(newIndex, 0, list.Count), project);
        // The list position is now authoritative; renumber before Normalize sorts by Order.
        Commit(list.Select((p, i) => p with { Order = i }).ToList());
    }

    private void Replace(ProjectDefinition updated) =>
        Commit(_projects.Select(p => p.Id == updated.Id ? updated : p).ToList());

    /// <summary>Saves the candidate list first; the in-memory state changes only if the save succeeded.</summary>
    /// <exception cref="ProjectsConflictException">Another instance changed the file; the registry was reloaded.</exception>
    private void Commit(List<ProjectDefinition> candidate, IReadOnlyList<JsonNode>? unrecognized = null)
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException("프로젝트 목록이 읽기 전용이라 변경할 수 없습니다. 시작 시 표시된 안내를 확인하세요.");
        }

        var normalized = Normalize(candidate);
        var raw = unrecognized ?? _unrecognized;
        if (!TryWriteFile(normalized, raw))
        {
            ReloadAfterConflict();
            Changed?.Invoke(this, EventArgs.Empty);
            _log.Warning("다른 DotDeck 창에서 프로젝트 목록이 변경되어 다시 불러왔습니다.");
            throw new ProjectsConflictException();
        }

        _projects = normalized;
        _unrecognized = raw;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Reloads the externally changed file. If it could not be read or written, the previously shown list is
    /// kept (instead of the defaults) and the registry stays read-only, so nothing is overwritten.
    /// </summary>
    private void ReloadAfterConflict()
    {
        var (projects, unrecognized, rootExtra) = (_projects, _unrecognized, _rootExtra);
        if (Load().Status == ProjectRegistryLoadStatus.ReadOnlyUnreadable)
        {
            (_projects, _unrecognized, _rootExtra) = (projects, unrecognized, rootExtra);
        }
    }

    /// <summary>
    /// Writes under a cross-process lock after checking that the file still matches what this instance
    /// last saw. Returns false (nothing written) on a conflicting external change.
    /// </summary>
    private bool TryWriteFile(IReadOnlyList<ProjectDefinition> projects, IReadOnlyList<JsonNode> unrecognized)
    {
        var text = ProjectsFileSerializer.Serialize(new ProjectsFileContent(projects, unrecognized, _rootExtra));
        using (_store.AcquireLock(LockPath, s_lockTimeout))
        {
            var current = ReadCurrentTextOrNull();
            if (!string.Equals(current, _lastKnownText, StringComparison.Ordinal))
            {
                return false;
            }

            WriteLocked(text);
        }

        return true;
    }

    /// <summary>Writes the file; the caller must hold the lock.</summary>
    private void WriteLocked(string text)
    {
        _store.WriteAllTextAtomic(_filePath, text);
        _lastKnownText = text;
    }

    private string? ReadCurrentTextOrNull()
    {
        if (!_store.FileExists(_filePath))
        {
            return null;
        }

        try
        {
            return _store.ReadAllText(_filePath);
        }
        catch (DecoderFallbackException)
        {
            // Unreadable text never equals what we last wrote: treat as an external change.
            return "\0invalid";
        }
    }

    private void ResetToDefaults()
    {
        _projects = Normalize(EnsureBuiltins([]));
        _unrecognized = [];
        _rootExtra = null;
    }

    private string? PreserveCorruptFile()
    {
        var stamp = _time.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var target = $"{_filePath}.corrupt-{stamp}";
        for (var n = 2; _store.FileExists(target); n++)
        {
            target = $"{_filePath}.corrupt-{stamp}-{n}";
        }

        try
        {
            _store.Move(_filePath, target);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error($"손상된 프로젝트 목록을 보존하지 못했습니다: {_filePath}", ex);
            return null;
        }
    }

    private static List<ProjectDefinition> EnsureBuiltins(IReadOnlyList<ProjectDefinition> loaded)
    {
        var result = loaded.ToList();
        var order = -BuiltinProjects.All.Count;
        foreach (var builtin in BuiltinProjects.All)
        {
            if (!result.Any(p => p.Builtin == builtin))
            {
                // Missing built-ins go first, in their canonical order.
                result.Add(BuiltinProjects.CreateDefault(builtin, order));
            }

            order++;
        }

        return result;
    }

    /// <summary>Stable sort by Order, then renumber 0..n-1.</summary>
    private static List<ProjectDefinition> Normalize(IEnumerable<ProjectDefinition> projects) =>
        projects
            .Select((p, index) => (p, index))
            .OrderBy(x => x.p.Order)
            .ThenBy(x => x.index)
            .Select((x, i) => x.p with { Order = i })
            .ToList();

    private ProjectDefinition Require(string id) =>
        Find(id) ?? throw new KeyNotFoundException($"프로젝트를 찾을 수 없습니다: {id}");

    private ProjectDefinition RequireFolder(string id, string builtinMessage)
    {
        var project = Require(id);
        return project.IsBuiltin ? throw new InvalidOperationException(builtinMessage) : project;
    }

    private string ValidateName(string name, string? exceptId)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("프로젝트 이름을 입력하세요.", nameof(name));
        }

        if (trimmed.Length > MaxNameLength)
        {
            throw new ArgumentException($"프로젝트 이름은 {MaxNameLength}자 이하여야 합니다.", nameof(name));
        }

        if (_projects.Any(p => p.Id != exceptId && string.Equals(p.Name, trimmed, StringComparison.CurrentCultureIgnoreCase)))
        {
            throw new ArgumentException($"같은 이름의 프로젝트가 이미 있습니다: {trimmed}", nameof(name));
        }

        return trimmed;
    }

    private string ValidateRoot(string root, string? exceptId)
    {
        var trimmed = root?.Trim().Trim('"') ?? string.Empty;
        if (trimmed.Length == 0 || !Path.IsPathFullyQualified(trimmed))
        {
            throw new ArgumentException("프로젝트 루트는 절대 경로여야 합니다.", nameof(root));
        }

        string normalized;
        try
        {
            normalized = ProjectsFileSerializer.NormalizeRoot(trimmed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException($"올바른 경로가 아닙니다: {trimmed}", nameof(root), ex);
        }

        var duplicate = _projects.FirstOrDefault(p =>
            p.Id != exceptId && p.Root is not null && string.Equals(p.Root, normalized, StringComparison.OrdinalIgnoreCase));
        if (duplicate is not null)
        {
            throw new ArgumentException($"이 폴더는 이미 '{duplicate.Name}' 프로젝트로 등록되어 있습니다.", nameof(root));
        }

        return normalized;
    }

    private static string? ValidateColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return null;
        }

        var trimmed = color.Trim();
        return ColorRegex().IsMatch(trimmed)
            ? trimmed.ToUpperInvariant()
            : throw new ArgumentException($"색상은 #RRGGBB 형식이어야 합니다: {color}", nameof(color));
    }

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex ColorRegex();
}
