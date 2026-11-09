using System.Text.Json;
using System.Text.Json.Nodes;
using DotDeck.Core.Files;

namespace DotDeck.Core.Settings;

public enum ExclusionKind { AutomaticBackup, ChangeReview }

public sealed record ExcludedPath(string Path, bool IsDirectory)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string KindText => IsDirectory ? "폴더" : "파일";
    public static string Normalize(string path)
    {
        var value = path.Trim().Trim('"').Replace('/', System.IO.Path.DirectorySeparatorChar);
        if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) value = @"\\" + value[8..];
        else if (value.StartsWith(@"\\?\", StringComparison.Ordinal)) value = value[4..];
        if (value.StartsWith(@"\\.\", StringComparison.Ordinal)) throw new ArgumentException("장치 경로는 제외항목으로 사용할 수 없습니다.");
        if (!System.IO.Path.IsPathFullyQualified(value) || value.IndexOfAny(['*', '?', '\r', '\n', '\0']) >= 0)
            throw new ArgumentException("와일드카드가 없는 전체 파일 또는 폴더 경로를 입력하세요.");
        return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(value));
    }
}

/// <summary>Explicit absolute paths only. Folder rules include descendants; file rules match only that file.</summary>
public sealed class PathExclusionPolicy
{
    public static PathExclusionPolicy Empty { get; } = new([]);
    private readonly IReadOnlyList<ExcludedPath> _entries;
    private readonly IFileStore? _store;
    public PathExclusionPolicy(IEnumerable<ExcludedPath> entries, IFileStore? store = null)
    {
        _store = store;
        _entries = entries.Select(e => e with { Path = ExcludedPath.Normalize(e.Path) }).ToArray();
    }
    public bool IsExcluded(string path) => Check(path, directoriesOnly: false);
    public bool IsDirectoryExcluded(string path) => Check(path, directoriesOnly: true);
    private bool Check(string path, bool directoriesOnly)
    {
        if (_entries.Count == 0) return false;
        var entries = directoriesOnly ? _entries.Where(e => e.IsDirectory).ToArray() : _entries;
        if (entries.Count == 0) return false;
        var full = ExcludedPath.Normalize(path);
        if (entries.Any(e => Matches(full, e.Path, e.IsDirectory))) return true;
        if (_store is null) return false;
        var final = Resolve(full);
        return entries.Any(e => Matches(final, Resolve(e.Path), e.IsDirectory));
    }
    private static bool Matches(string path, string rule, bool directory) =>
        path.Equals(rule, StringComparison.OrdinalIgnoreCase)
        || directory && path.StartsWith(System.IO.Path.EndsInDirectorySeparator(rule) ? rule : rule + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private string Resolve(string path)
    {
        // Resolve linked ancestors too, without opening file contents. Missing files can still match a folder.
        try
        {
            for (var hop = 0; hop < 40; hop++)
            {
                var prefix = System.IO.Path.GetPathRoot(path)!;
                var parts = path[prefix.Length..].Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
                var changed = false;
                for (var i = 0; i < parts.Length; i++)
                {
                    prefix = System.IO.Path.Combine(prefix, parts[i]);
                    if (_store!.GetLinkTarget(prefix) is not { } target) continue;
                    path = ExcludedPath.Normalize(System.IO.Path.Combine([target, .. parts.Skip(i + 1)]));
                    changed = true;
                    break;
                }
                if (!changed) return path;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return path;
    }
}

/// <summary>Independent lists in one settings file. Publish new policies only after the atomic save succeeds.</summary>
public sealed class PathExclusionStore
{
    private readonly IFileStore _store;
    private readonly string _path;
    private readonly Lock _gate = new();
    private JsonObject _document;
    private State _state;
    private sealed record State(IReadOnlyList<ExcludedPath> Backup, IReadOnlyList<ExcludedPath> Review, PathExclusionPolicy BackupPolicy, PathExclusionPolicy ReviewPolicy);
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    public PathExclusionStore(IFileStore store, string path, bool recoverLoadFailure = false)
    {
        _store = store;
        _path = path;
        try
        {
            _document = store.FileExists(path) ? JsonNode.Parse(store.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException("제외항목 설정 형식이 잘못되었습니다.") : new JsonObject();
            _state = Build(Read("automaticBackup"), Read("changeReview"));
        }
        catch (Exception ex) when (recoverLoadFailure && ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            _document = new JsonObject();
            _state = Build([], []);
            LoadWarning = "제외항목 설정을 읽지 못했습니다. 이번 실행에서는 제외항목을 적용하지 않으며 원본을 보호하기 위해 설정 저장을 차단합니다. 파일을 복구한 뒤 앱을 다시 시작하세요: " + path;
        }
    }
    public string? LoadWarning { get; }
    public PathExclusionPolicy BackupPolicy => Volatile.Read(ref _state).BackupPolicy;
    public PathExclusionPolicy ReviewPolicy => Volatile.Read(ref _state).ReviewPolicy;
    public IReadOnlyList<ExcludedPath> Get(ExclusionKind kind) => kind == ExclusionKind.AutomaticBackup ? Volatile.Read(ref _state).Backup : Volatile.Read(ref _state).Review;
    public event EventHandler? Changed;
    public void Replace(IReadOnlyList<ExcludedPath> backup, IReadOnlyList<ExcludedPath> review)
    {
        if (LoadWarning is { } warning) throw new IOException(warning);
        lock (_gate)
        {
            var next = Build(backup, review);
            var document = (JsonObject)_document.DeepClone();
            document["automaticBackup"] = JsonSerializer.SerializeToNode(next.Backup, Options);
            document["changeReview"] = JsonSerializer.SerializeToNode(next.Review, Options);
            _store.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            _store.WriteAllTextAtomic(_path, document.ToJsonString(Options));
            _document = document;
            Volatile.Write(ref _state, next);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public void Add(ExclusionKind kind, string path, bool directory = false)
    {
        lock (_gate)
        {
            var state = _state;
            var item = new ExcludedPath(path, directory);
            Replace(kind == ExclusionKind.AutomaticBackup ? [.. state.Backup, item] : state.Backup,
                kind == ExclusionKind.ChangeReview ? [.. state.Review, item] : state.Review);
        }
    }
    private IReadOnlyList<ExcludedPath> Read(string key) => _document[key] is null ? [] : _document[key]!.Deserialize<ExcludedPath[]>(Options)
        ?? throw new InvalidDataException("제외항목 설정을 읽을 수 없습니다.");
    private State Build(IEnumerable<ExcludedPath> backup, IEnumerable<ExcludedPath> review)
    {
        static IReadOnlyList<ExcludedPath> Normalize(IEnumerable<ExcludedPath> values) => values
            .Select(e => e is null || e.Path is null ? throw new InvalidDataException("제외항목 경로가 누락되었습니다.") : e with { Path = ExcludedPath.Normalize(e.Path) })
            .DistinctBy(e => (e.Path.ToUpperInvariant(), e.IsDirectory)).ToArray();
        var a = Normalize(backup); var b = Normalize(review);
        return new(a, b, new(a, _store), new(b, _store));
    }
}
