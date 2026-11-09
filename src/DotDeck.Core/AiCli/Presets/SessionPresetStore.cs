using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotDeck.Core.AiCli.Launch;
using DotDeck.Core.Files;
using DotDeck.Core.Projects;

namespace DotDeck.Core.AiCli.Presets;

/// <summary>A saved session start (SPEC 11.4). Projects are referenced by id so renames and root moves apply automatically.</summary>
public sealed record SessionPreset
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string PrimaryProjectId { get; init; }

    public IReadOnlyList<string> AdditionalProjectIds { get; init; } = [];

    public CliKind Cli { get; init; } = CliKind.Claude;

    public LaunchMode Mode { get; init; } = LaunchMode.Interactive;

    public bool IncludeOpenDocuments { get; init; } = true;

    public bool ResumeConversation { get; init; }

    public bool AllowReadOnlyShell { get; init; }

    /// <summary>Fields written by a newer version, kept on save.</summary>
    public JsonObject? Extra { get; init; }
}

/// <summary>A preset checked against the current project list.</summary>
public sealed record PresetResolution(SessionPreset Preset, IReadOnlyList<string> MissingProjectIds)
{
    /// <summary>The primary project must exist; missing additional projects only produce a warning.</summary>
    public bool CanLaunch { get; init; }

    public bool HasWarning => MissingProjectIds.Count > 0;
}

/// <summary>Remembered CLI conversation ids for one scope key.</summary>
public sealed record ConversationIds(string? ClaudeSessionId, string? CodexThreadId);

public enum PresetStoreLoadStatus
{
    Loaded,
    Missing,

    /// <summary>The file could not be parsed; it was kept as <c>.corrupt-*</c> and an empty list is used.</summary>
    RecoveredFromCorrupt,

    /// <summary>Written by a newer DotDeck or not readable: changes are refused so the file is never overwritten.</summary>
    ReadOnly,
}

/// <summary>
/// <c>sessions.json</c> (DESIGN "데이터 파일"): presets, the last additional projects per primary project and the CLI
/// conversation ids per scope. Unknown fields at the root and per preset are kept; entries that cannot be read are
/// written back verbatim. Memory changes only after a successful save.
/// </summary>
public sealed class SessionPresetStore
{
    public const int SchemaVersion = 1;

    private static readonly HashSet<string> s_rootKeys = ["schemaVersion", "presets", "lastAdditional", "conversations"];

    private static readonly HashSet<string> s_presetKeys =
        ["id", "name", "primary", "additional", "cli", "mode", "includeOpenDocuments", "resume", "allowReadOnlyShell"];

    private static readonly JsonSerializerOptions s_write = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
    };

    private readonly IFileStore _store;
    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private State _state = State.Empty;

    public SessionPresetStore(IFileStore store, string path, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(path);
        _store = store;
        _path = path;
        _time = time ?? TimeProvider.System;
    }

    public PresetStoreLoadStatus Status { get; private set; } = PresetStoreLoadStatus.Missing;

    /// <summary>Where a corrupt file was moved, when <see cref="Status"/> is <see cref="PresetStoreLoadStatus.RecoveredFromCorrupt"/>.</summary>
    public string? CorruptCopyPath { get; private set; }

    public IReadOnlyList<SessionPreset> Presets
    {
        get
        {
            lock (_gate)
            {
                return [.. _state.Presets.Select(Copy)];
            }
        }
    }

    public PresetStoreLoadStatus Load()
    {
        lock (_gate)
        {
            CorruptCopyPath = null;
            try
            {
                using var _ = _store.AcquireLock(LockPath, LockTimeout);
                switch (ReadFile(out var state))
                {
                    case ReadResult.Missing:
                        _state = State.Empty;
                        return Status = PresetStoreLoadStatus.Missing;
                    case ReadResult.Ok:
                        _state = state!;
                        return Status = PresetStoreLoadStatus.Loaded;
                    case ReadResult.Corrupt:
                        _state = State.Empty;
                        CorruptCopyPath = PreserveCorrupt();
                        return Status = CorruptCopyPath is null ? PresetStoreLoadStatus.ReadOnly : PresetStoreLoadStatus.RecoveredFromCorrupt;
                    default:
                        _state = State.Empty;
                        return Status = PresetStoreLoadStatus.ReadOnly;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _state = State.Empty;
                return Status = PresetStoreLoadStatus.ReadOnly;
            }
        }
    }

    private enum ReadResult
    {
        Missing,
        Ok,
        Corrupt,
        Unusable,
    }

    private string LockPath => _path + ".lock";

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Reads the file as it is now. Unusable: unreadable or written by a newer version (never overwrite).</summary>
    private ReadResult ReadFile(out State? state)
    {
        state = null;
        if (!_store.FileExists(_path))
        {
            return ReadResult.Missing;
        }

        string json;
        try
        {
            json = _store.ReadAllText(_path);
        }
        catch (DecoderFallbackException)
        {
            return ReadResult.Corrupt;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ReadResult.Unusable;
        }

        try
        {
            state = Parse(json);
            return state is null ? ReadResult.Unusable : ReadResult.Ok;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or FormatException)
        {
            return ReadResult.Corrupt;
        }
    }

    private static SessionPreset Copy(SessionPreset preset) => preset with
    {
        AdditionalProjectIds = [.. preset.AdditionalProjectIds],
        Extra = preset.Extra?.DeepClone() as JsonObject,
    };

    /// <summary>Adds the preset or replaces the one with the same id.</summary>
    public void SavePreset(SessionPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentException.ThrowIfNullOrWhiteSpace(preset.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(preset.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(preset.PrimaryProjectId);
        var incoming = Copy(preset);
        Update(s =>
        {
            var list = s.Presets.ToList();
            var index = list.FindIndex(p => p.Id == incoming.Id);
            if (index >= 0)
            {
                // Fields this version does not know survive an edit made with a freshly built preset object.
                list[index] = incoming with { Extra = MergeExtra(list[index].Extra, incoming.Extra) };
            }
            else
            {
                list.Add(incoming);
            }

            return s with { Presets = list, UnreadablePresets = WithoutId(s.UnreadablePresets, incoming.Id) };
        });
    }

    /// <summary>Removes the preset and any unreadable or duplicate entry with the same id (so it cannot come back).</summary>
    public void RemovePreset(string id) =>
        Update(s => s with { Presets = s.Presets.Where(p => p.Id != id).ToList(), UnreadablePresets = WithoutId(s.UnreadablePresets, id) });

    private static List<JsonNode> WithoutId(IEnumerable<JsonNode> entries, string id) =>
        [.. entries.Where(e => !(e is JsonObject o && Text(o["id"]) == id))];

    private static JsonObject? MergeExtra(JsonObject? stored, JsonObject? incoming)
    {
        if (stored is null)
        {
            return incoming;
        }

        var merged = (JsonObject)stored.DeepClone();
        foreach (var (key, value) in incoming ?? [])
        {
            merged[key] = value?.DeepClone();
        }

        return merged;
    }

    /// <summary>The additional projects last used with <paramref name="primaryProjectId"/> (the next default).</summary>
    public IReadOnlyList<string> GetLastAdditional(string primaryProjectId)
    {
        lock (_gate)
        {
            return _state.LastAdditional.TryGetValue(primaryProjectId, out var ids) ? ids : [];
        }
    }

    public void SetLastAdditional(string primaryProjectId, IReadOnlyList<string> additionalProjectIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryProjectId);
        var ids = additionalProjectIds.Where(i => !string.IsNullOrWhiteSpace(i) && i != primaryProjectId).Distinct(StringComparer.Ordinal).ToList();
        Update(s =>
        {
            var map = new Dictionary<string, IReadOnlyList<string>>(s.LastAdditional, StringComparer.Ordinal) { [primaryProjectId] = ids };
            return s with { LastAdditional = map };
        });
    }

    public ConversationIds? GetConversation(string scopeKey)
    {
        lock (_gate)
        {
            return _state.Conversations.TryGetValue(scopeKey, out var ids) ? ids : null;
        }
    }

    /// <summary>Remembers the CLI's conversation id for a scope; null forgets it.</summary>
    public void SetConversation(string scopeKey, CliKind cli, string? id)
    {
        ArgumentException.ThrowIfNullOrEmpty(scopeKey);
        Update(s =>
        {
            var map = new Dictionary<string, ConversationIds>(s.Conversations, StringComparer.Ordinal);
            var current = map.GetValueOrDefault(scopeKey) ?? new ConversationIds(null, null);
            var next = cli == CliKind.Claude ? current with { ClaudeSessionId = id } : current with { CodexThreadId = id };
            if (next.ClaudeSessionId is null && next.CodexThreadId is null)
            {
                map.Remove(scopeKey);
            }
            else
            {
                map[scopeKey] = next;
            }

            return s with { Conversations = map };
        });
    }

    /// <summary>Checks a preset's project ids against the current project list (renames apply automatically).</summary>
    public static PresetResolution Resolve(SessionPreset preset, IReadOnlyList<ProjectDefinition> projects)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(projects);
        var ids = projects.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var missing = new[] { preset.PrimaryProjectId }.Concat(preset.AdditionalProjectIds).Where(id => !ids.Contains(id)).Distinct(StringComparer.Ordinal).ToList();
        return new PresetResolution(preset, missing) { CanLaunch = ids.Contains(preset.PrimaryProjectId) };
    }

    private void Update(Func<State, State> change)
    {
        lock (_gate)
        {
            if (Status == PresetStoreLoadStatus.ReadOnly)
            {
                throw new InvalidOperationException("sessions.json은 읽기 전용 상태라 저장할 수 없습니다.");
            }

            // Another DotDeck instance may have saved since we loaded: under a cross-process lock, apply the change
            // to the file as it is now. A file that became unreadable or newer is never overwritten.
            _store.CreateDirectory(Path.GetDirectoryName(_path)!);
            using var _ = _store.AcquireLock(LockPath, LockTimeout);
            var current = ReadFile(out var disk) switch
            {
                ReadResult.Ok => disk!,
                ReadResult.Missing => State.Empty,
                _ => throw new InvalidOperationException("sessions.json이 다른 곳에서 바뀌어 읽을 수 없게 되었습니다. 저장하지 않았습니다."),
            };

            var next = change(current);
            _store.WriteAllTextAtomic(_path, Serialize(next));
            _state = next;
            if (Status is PresetStoreLoadStatus.Missing or PresetStoreLoadStatus.RecoveredFromCorrupt)
            {
                Status = PresetStoreLoadStatus.Loaded;
            }
        }
    }

    private string? PreserveCorrupt()
    {
        var stamp = _time.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var target = $"{_path}.corrupt-{stamp}";
        for (var n = 2; _store.FileExists(target); n++)
        {
            target = $"{_path}.corrupt-{stamp}-{n}";
        }

        try
        {
            _store.Move(_path, target);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record State(
        IReadOnlyList<SessionPreset> Presets,
        IReadOnlyList<JsonNode> UnreadablePresets,
        IReadOnlyDictionary<string, IReadOnlyList<string>> LastAdditional,
        IReadOnlyDictionary<string, ConversationIds> Conversations,
        JsonObject? RootExtra)
    {
        public static State Empty { get; } = new([], [], new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, ConversationIds>(), null);
    }

    /// <summary>Null when the schema is newer than this version (the file must not be overwritten).</summary>
    private static State? Parse(string json)
    {
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
            ?? throw new JsonException("sessions.json must contain an object.");

        if (root["schemaVersion"] is { } versionNode)
        {
            if (versionNode is not JsonValue v || !v.TryGetValue<int>(out var version))
            {
                throw new JsonException("schemaVersion must be an integer.");
            }

            if (version > SchemaVersion)
            {
                return null;
            }
        }

        var presets = new List<SessionPreset>();
        var unreadable = new List<JsonNode>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        switch (root["presets"])
        {
            case null:
                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    if (item is JsonObject entry && ReadPreset(entry) is { } preset && seen.Add(preset.Id))
                    {
                        presets.Add(preset);
                    }
                    else if (item is not null)
                    {
                        unreadable.Add(item.DeepClone());
                    }
                }

                break;
            default:
                throw new JsonException("presets must be an array.");
        }

        var last = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (root["lastAdditional"] is JsonObject lastObj)
        {
            foreach (var (key, value) in lastObj)
            {
                if (StringList(value) is { } ids)
                {
                    last[key] = ids;
                }
            }
        }

        var conversations = new Dictionary<string, ConversationIds>(StringComparer.Ordinal);
        if (root["conversations"] is JsonObject convObj)
        {
            foreach (var (key, value) in convObj)
            {
                if (value is JsonObject c)
                {
                    var claude = Text(c["claude"]?["sessionId"]);
                    var codex = Text(c["codex"]?["threadId"]);
                    if (claude is not null || codex is not null)
                    {
                        conversations[key] = new ConversationIds(claude, codex);
                    }
                }
            }
        }

        return new State(presets, unreadable, last, conversations, Extra(root, s_rootKeys));
    }

    private static SessionPreset? ReadPreset(JsonObject entry)
    {
        var id = Text(entry["id"]);
        var name = Text(entry["name"]);
        var primary = Text(entry["primary"]);
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(primary))
        {
            return null;
        }

        var additional = entry["additional"] is null ? [] : StringList(entry["additional"]);
        CliKind? cli = Text(entry["cli"]) switch { null or "claude" => CliKind.Claude, "codex" => CliKind.Codex, _ => null };
        LaunchMode? mode = Text(entry["mode"]) switch { null or "interactive" => LaunchMode.Interactive, "nonInteractive" => LaunchMode.NonInteractive, _ => null };
        if (additional is null || cli is null || mode is null
            || !Bool(entry, "includeOpenDocuments", true, out var includeDocs)
            || !Bool(entry, "resume", false, out var resume)
            || !Bool(entry, "allowReadOnlyShell", false, out var shell))
        {
            return null;
        }

        return new SessionPreset
        {
            Id = id,
            Name = name,
            PrimaryProjectId = primary,
            AdditionalProjectIds = additional,
            Cli = cli.Value,
            Mode = mode.Value,
            IncludeOpenDocuments = includeDocs,
            ResumeConversation = resume,
            AllowReadOnlyShell = shell,
            Extra = Extra(entry, s_presetKeys),
        };
    }

    private static string Serialize(State state)
    {
        var presets = new JsonArray();
        foreach (var p in state.Presets)
        {
            var obj = new JsonObject
            {
                ["id"] = p.Id,
                ["name"] = p.Name,
                ["primary"] = p.PrimaryProjectId,
                ["additional"] = new JsonArray(p.AdditionalProjectIds.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()),
                ["cli"] = p.Cli == CliKind.Claude ? "claude" : "codex",
                ["mode"] = p.Mode == LaunchMode.Interactive ? "interactive" : "nonInteractive",
                ["includeOpenDocuments"] = p.IncludeOpenDocuments,
                ["resume"] = p.ResumeConversation,
                ["allowReadOnlyShell"] = p.AllowReadOnlyShell,
            };
            CopyExtra(p.Extra, obj, s_presetKeys);
            presets.Add(obj);
        }

        foreach (var raw in state.UnreadablePresets)
        {
            presets.Add(raw.DeepClone());
        }

        var last = new JsonObject();
        foreach (var (key, ids) in state.LastAdditional.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            last[key] = new JsonArray(ids.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
        }

        var conversations = new JsonObject();
        foreach (var (key, ids) in state.Conversations.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var obj = new JsonObject();
            if (ids.ClaudeSessionId is not null)
            {
                obj["claude"] = new JsonObject { ["sessionId"] = ids.ClaudeSessionId };
            }

            if (ids.CodexThreadId is not null)
            {
                obj["codex"] = new JsonObject { ["threadId"] = ids.CodexThreadId };
            }

            conversations[key] = obj;
        }

        var root = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["presets"] = presets,
            ["lastAdditional"] = last,
            ["conversations"] = conversations,
        };
        CopyExtra(state.RootExtra, root, s_rootKeys);
        return root.ToJsonString(s_write);
    }

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool Bool(JsonObject obj, string key, bool fallback, out bool value)
    {
        value = fallback;
        var node = obj[key];
        return node is null || (node is JsonValue v && v.TryGetValue(out value));
    }

    private static List<string>? StringList(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return null;
        }

        var list = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (Text(item) is not { } s)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(s) && !list.Contains(s, StringComparer.Ordinal))
            {
                list.Add(s);
            }
        }

        return list;
    }

    private static JsonObject? Extra(JsonObject source, HashSet<string> known)
    {
        JsonObject? extra = null;
        foreach (var (key, value) in source)
        {
            if (!known.Contains(key))
            {
                extra ??= [];
                extra[key] = value?.DeepClone();
            }
        }

        return extra;
    }

    private static void CopyExtra(JsonObject? extra, JsonObject target, HashSet<string> known)
    {
        if (extra is null)
        {
            return;
        }

        foreach (var (key, value) in extra)
        {
            if (!known.Contains(key))
            {
                target[key] = value?.DeepClone();
            }
        }
    }
}
