using DotDeck.Core.FileOperations;
using DotDeck.Core.Files;
using DotDeck.Core.Projects;

namespace DotDeck.Core.Explorer;

/// <summary>What may be registered as a "사용자 지정 파일" (System project and folder projects alike).</summary>
public static class CustomFileRules
{
    /// <summary>The node's name in every project.</summary>
    public const string NodeName = "사용자 지정 파일";

    /// <summary>Returns why <paramref name="path"/> cannot be registered, or null when it can.</summary>
    public static string? Validate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return "전체 경로를 입력해야 합니다.";
        }

        var name = Path.GetFileName(path);
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return "파일 경로가 올바르지 않습니다.";
        }

        return SecretFilePolicy.IsSecret(name) ? "비밀 파일은 사용자 지정 파일에 추가할 수 없습니다." : null;
    }

    /// <summary>
    /// Follows renames and moves (the file itself, or a folder above it). Deleted files stay on the list and show as
    /// missing, like the System project's list. A file renamed into one that may not be registered (a secret file)
    /// leaves the list. Returns the same instance when nothing changed.
    /// </summary>
    public static IReadOnlyList<string> Rewrite(IReadOnlyList<string> files, IReadOnlyList<PathChange> changes)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(changes);
        if (files.Count == 0)
        {
            return files;
        }

        var result = files.ToList();
        var changed = false;
        foreach (var move in changes.OfType<PathChange.Moved>())
        {
            var from = Path.TrimEndingDirectorySeparator(move.From);
            var to = Path.TrimEndingDirectorySeparator(move.To);
            for (var i = 0; i < result.Count; i++)
            {
                var path = result[i];
                string? moved = null;
                if (string.Equals(path, from, StringComparison.OrdinalIgnoreCase))
                {
                    moved = to;
                }
                else if (path.StartsWith(from + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    moved = to + path[from.Length..];
                }

                if (moved is not null && !string.Equals(moved, path, StringComparison.Ordinal))
                {
                    result[i] = moved;
                    changed = true;
                }
            }
        }

        if (!changed)
        {
            return files;
        }

        return result.Where(p => Validate(p) is null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>File nodes of a list; a missing file is shown as "이름 (없음)". Keys are full paths.</summary>
    public static List<NodeInfo> CreateNodes(IFileStore store, IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(paths);
        return paths.Select(path =>
        {
            FileSystemEntry? entry;
            try
            {
                entry = store.GetEntry(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                entry = null;
            }

            var name = Path.GetFileName(path);
            var exists = entry is { IsDirectory: false };
            return new NodeInfo
            {
                Id = path,
                Kind = NodeKind.File,
                Name = exists ? name : $"{name} (없음)",
                FullPath = path,
                RelativePath = null,
                FileKind = FileKindClassifier.Classify(name),
                IsSecret = SecretFilePolicy.IsSecret(name),
                IsReadOnly = entry?.IsReadOnly ?? false,
                IsCustomFile = true,
                Length = entry?.Length ?? 0,
                LastWriteTimeUtc = entry?.LastWriteTimeUtc ?? default,
            };
        }).ToList();
    }
}

/// <summary>
/// Adds a folder project's "사용자 지정 파일" node (SPEC 2.3) at the top of another source. The node's id cannot be
/// a relative path (':' is not allowed in file names), so its expansion key never meets a real folder's.
/// </summary>
public sealed class CustomFilesProjectSource : IProjectSource
{
    public const string NodeId = "::custom-files";

    private readonly IFileStore _store;
    private readonly IReadOnlyList<string> _files;

    public CustomFilesProjectSource(IProjectSource inner, IFileStore store, IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(files);
        Inner = inner;
        _store = store;
        _files = files;
    }

    public IProjectSource Inner { get; }

    public ProjectDefinition Project => Inner.Project;

    public string? RootPath => Inner.RootPath;

    public ProjectRootState GetRootState() => Inner.GetRootState();

    public static NodeInfo Node { get; } = new() { Id = NodeId, Kind = NodeKind.Folder, Name = CustomFileRules.NodeName };

    public IReadOnlyList<NodeInfo> GetChildren(NodeInfo? parent)
    {
        if (parent is null)
        {
            return [Node, .. Inner.GetChildren(null)];
        }

        return parent.Id == NodeId ? CustomFileRules.CreateNodes(_store, _files) : Inner.GetChildren(parent);
    }

    /// <summary>
    /// The node alone (with its files), for when the inner source has no listing (root missing or unreadable): the
    /// files may live anywhere, so they stay reachable.
    /// </summary>
    public CustomFilesProjectSource NodeOnly() => new(new EmptySource(Inner), _store, _files);

    /// <summary>The source below any wrapper.</summary>
    public static IProjectSource Unwrap(IProjectSource source) => source is CustomFilesProjectSource custom ? custom.Inner : source;

    private sealed class EmptySource(IProjectSource inner) : IProjectSource
    {
        public ProjectDefinition Project => inner.Project;

        public string? RootPath => inner.RootPath;

        public ProjectRootState GetRootState() => ProjectRootState.Ready;

        public IReadOnlyList<NodeInfo> GetChildren(NodeInfo? parent) => [];
    }
}
