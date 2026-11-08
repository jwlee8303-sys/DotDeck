using System.Text.Json;
using System.Text.Json.Nodes;
using DotDeck.Core.Editors;
using DotDeck.Core.Files;

namespace DotDeck.Core.Roles;

/// <summary>
/// Editors the user chose with "다음 형식으로 열기", per file path, in <c>%APPDATA%\DotDeck\editor-overrides.json</c>:
/// <code>{ "schemaVersion": 1, "overrides": { "D:\\p\\x.json": "settingsForm" } }</code>
/// Entries with editor names this version does not know are kept as they are.
/// </summary>
public sealed class EditorOverrideStore
{
    private static readonly JsonSerializerOptions s_write = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
    };

    private readonly IFileStore _store;
    private readonly string _path;
    // _gate guards the in-memory map (short holds only); _write orders saves so file I/O never blocks readers.
    private readonly Lock _gate = new();
    private readonly Lock _write = new();
    private Dictionary<string, string> _entries = new(StringComparer.OrdinalIgnoreCase);

    public EditorOverrideStore(IFileStore store, string path)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(path);
        _store = store;
        _path = path;
        Load();
    }

    /// <summary>The chosen editor for <paramref name="path"/>, or null for automatic detection.</summary>
    public EditorKind? Get(string path)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(Key(path), out var name) && NameToKind(name) is { } kind ? kind : null;
        }
    }

    /// <summary>Remembers <paramref name="kind"/> for <paramref name="path"/>; null returns it to automatic detection.</summary>
    public void Set(string path, EditorKind? kind)
    {
        var key = Key(path);
        lock (_write)
        {
            Dictionary<string, string> next;
            lock (_gate)
            {
                next = new Dictionary<string, string>(_entries, StringComparer.OrdinalIgnoreCase);
            }

            if (kind is { } k)
            {
                next[key] = KindToName(k);
            }
            else if (!next.Remove(key))
            {
                return;
            }

            // Memory changes only after the file is written: a failed choice never applies to later views or saves.
            Save(next);
            lock (_gate)
            {
                _entries = next;
            }
        }
    }

    public static string KindToName(EditorKind kind) => Editors.Registry.EditorRegistry.Get(kind).Name;

    public static EditorKind? NameToKind(string name) => Editors.Registry.EditorRegistry.FindKind(name);

    private static string Key(string path) => FileRoleResolver.Normalize(path);

    private void Load()
    {
        try
        {
            if (!_store.FileExists(_path))
            {
                return;
            }

            if (JsonNode.Parse(_store.ReadAllText(_path)) is JsonObject { } root && root["overrides"] is JsonObject overrides)
            {
                foreach (var (key, value) in overrides)
                {
                    if (value is JsonValue v && v.TryGetValue<string>(out var name))
                    {
                        _entries[key] = name;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A damaged file means "no overrides"; it is replaced on the next change.
            _entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save(Dictionary<string, string> entries)
    {
        var overrides = new JsonObject();
        foreach (var (key, value) in entries.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            overrides[key] = value;
        }

        var root = new JsonObject { ["schemaVersion"] = 1, ["overrides"] = overrides };
        _store.CreateDirectory(Path.GetDirectoryName(_path)!);
        _store.WriteAllTextAtomic(_path, root.ToJsonString(s_write));
    }
}
