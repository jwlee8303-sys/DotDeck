using System.Text.Json;
using System.Text.Json.Nodes;
using DotDeck.Core.Files;
using DotDeck.Core.Logging;

namespace DotDeck.Core.Explorer;

/// <summary>Saved view state of one project's explorer.</summary>
/// <param name="Expanded">Expanded folder keys (<see cref="ExplorerTreeBuilder.KeyOf"/>).</param>
/// <param name="Selected">Key of the selected node, if any.</param>
public sealed record ExplorerViewState(bool IsCollapsed, IReadOnlyList<string> Expanded, string? Selected)
{
    public static ExplorerViewState Default { get; } = new(false, [], null);
}

/// <summary>
/// Reads and writes session.json (explorer state; later also open documents). Best effort: the session is a
/// convenience, so failures are logged and never thrown. Unknown top-level sections are preserved.
/// </summary>
public sealed class SessionStateStore
{
    public const int SchemaVersion = 1;

    /// <summary>Upper bound per project so a long-lived session file stays small.</summary>
    public const int MaxExpandedPerProject = 2000;

    private const string ExplorersKey = "explorers";
    private static readonly JsonSerializerOptions s_writeOptions = new() { WriteIndented = true };

    private readonly IFileStore _store;
    private readonly string _filePath;
    private readonly IAppLog _log;

    public SessionStateStore(IFileStore store, string filePath, IAppLog log)
    {
        _store = store;
        _filePath = filePath;
        _log = log;
    }

    public IReadOnlyDictionary<string, ExplorerViewState> LoadExplorers()
    {
        var result = new Dictionary<string, ExplorerViewState>(StringComparer.Ordinal);
        if (ReadRoot(out _)?[ExplorersKey] is not JsonObject explorers)
        {
            return result;
        }

        try
        {
            foreach (var (id, node) in explorers)
            {
                try
                {
                    if (node is JsonObject obj && TryReadState(obj) is { } state)
                    {
                        result[id] = state;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
                {
                    // Nested duplicate keys and the like only lose that one project's state.
                    _log.Warning($"세션 상태의 '{id}' 항목을 읽지 못해 무시합니다.", ex);
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _log.Warning("세션 상태의 탐색기 항목을 읽지 못해 기본값을 사용합니다.", ex);
            result.Clear();
        }

        return result;
    }

    public void SaveExplorers(IReadOnlyDictionary<string, ExplorerViewState> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        try
        {
            var root = ReadRoot(out var isNewer) ?? [];
            if (isNewer)
            {
                // Written by a newer DotDeck: never downgrade it.
                return;
            }

            root["schemaVersion"] = SchemaVersion;
            var explorers = new JsonObject();
            foreach (var (id, state) in states.OrderBy(s => s.Key, StringComparer.Ordinal))
            {
                var expanded = new JsonArray();
                foreach (var key in state.Expanded.Take(MaxExpandedPerProject))
                {
                    expanded.Add(key);
                }

                explorers[id] = new JsonObject
                {
                    ["collapsed"] = state.IsCollapsed,
                    ["expanded"] = expanded,
                    ["selected"] = state.Selected,
                };
            }

            root[ExplorersKey] = explorers;
            _store.WriteAllTextAtomic(_filePath, root.ToJsonString(s_writeOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning($"세션 상태를 저장하지 못했습니다: {_filePath}", ex);
        }
    }

    /// <summary>Returns the parsed root, or null when missing/unreadable (a fresh file is then written).</summary>
    private JsonObject? ReadRoot(out bool isNewerSchema)
    {
        isNewerSchema = false;
        try
        {
            if (!_store.FileExists(_filePath))
            {
                return null;
            }

            if (JsonNode.Parse(_store.ReadAllText(_filePath)) is not JsonObject obj)
            {
                _log.Warning($"세션 상태 형식을 알 수 없어 무시합니다: {_filePath}");
                return null;
            }

            // Count forces the property dictionary to build, so duplicate keys surface here.
            _ = obj.Count;
            var version = SchemaVersion;
            if (obj["schemaVersion"] is { } versionNode && !(versionNode is JsonValue v && v.TryGetValue(out version)))
            {
                _log.Warning($"세션 상태 형식을 알 수 없어 무시합니다: {_filePath}");
                return null;
            }

            if (version > SchemaVersion)
            {
                isNewerSchema = true;
                return null;
            }

            return obj;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or System.Text.DecoderFallbackException or ArgumentException or InvalidOperationException)
        {
            _log.Warning($"세션 상태를 읽지 못해 기본값을 사용합니다: {_filePath}", ex);
            return null;
        }
    }

    private static ExplorerViewState? TryReadState(JsonObject obj)
    {
        var collapsed = obj["collapsed"] is JsonValue c && c.TryGetValue<bool>(out var b) && b;
        var selected = obj["selected"] is JsonValue s && s.TryGetValue<string>(out var text) ? text : null;
        var expanded = new List<string>();
        if (obj["expanded"] is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is JsonValue v && v.TryGetValue<string>(out var key) && !string.IsNullOrEmpty(key))
                {
                    expanded.Add(key);
                }
            }
        }

        return new ExplorerViewState(collapsed, expanded, selected);
    }
}
