using System.Text.Json;
using System.Text.Json.Nodes;
using DotDeck.Core.Explorer.Visibility;

namespace DotDeck.Core.Projects;

/// <summary>Parsed projects.json content.</summary>
public sealed record ProjectsFileContent(
    IReadOnlyList<ProjectDefinition> Projects,
    IReadOnlyList<JsonNode> UnrecognizedEntries,
    JsonObject? RootExtra)
{
    /// <summary>Schema version of the file that was read (writing always uses the current one).</summary>
    public int FileVersion { get; init; } = ProjectsFileSerializer.SchemaVersion;
}

/// <summary>
/// Reads and writes projects.json (schema v2; v1 files are read and migrated). Unknown fields — at the root and per project — are kept.
/// An entry is loaded only if every known field is valid; otherwise the whole entry is written back verbatim,
/// so values this version does not understand (e.g. a future filter mode) are never rewritten.
/// </summary>
public static class ProjectsFileSerializer
{
    public const int SchemaVersion = 2;

    /// <summary>
    /// The v1 "configDocs" filter as an extension list: its extensions plus the dotfiles users keep most. Other
    /// extension-less dotfiles and .env* files are no longer shown by default.
    /// </summary>
    public static IReadOnlyList<string> LegacyConfigDocsExtensions { get; } = ExtensionRules.NormalizeAll(
    [
        ".md", ".json", ".jsonc", ".toml", ".yaml", ".yml", ".ps1", ".py", ".cs", ".xaml", ".props", ".txt",
        ".csproj", ".slnx", ".ini", ".xml", ".cmd", ".bat", ".sh", ".psm1", ".gitignore", ".editorconfig",
    ]);
    public const string BuiltinIdPrefix = "builtin.";

    private static readonly HashSet<string> s_knownRootKeys = ["schemaVersion", "projects"];

    private static readonly HashSet<string> s_knownProjectKeys =
        ["id", "kind", "builtinKey", "name", "root", "color", "order", "hidden", "excludedFolders", "visibility", "view",
         // v1 fields: read for migration, never written.
         "filter", "visibleItems"];

    private static readonly HashSet<string> s_knownVisibilityKeys = ["defaultExtensions", "showAllFiles", "rules"];

    private static readonly HashSet<string> s_knownViewKeys = ["mode", "flatSort", "flatOrder", "pins"];

    // Keep non-ASCII (Korean names, paths) readable for users who open the file; HTML-sensitive characters
    // are still escaped by the encoder.
    private static readonly JsonSerializerOptions s_writeOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
    };

    /// <exception cref="ProjectsFileException">The JSON is malformed or the schema is not supported.</exception>
    public static ProjectsFileContent Parse(string json)
    {
        try
        {
            return ParseCore(json);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException)
        {
            // Includes duplicate property names, which JsonObject reports as ArgumentException on access.
            throw new ProjectsFileException("projects.json is not valid.", ex);
        }
    }

    public static string Serialize(ProjectsFileContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var root = new JsonObject { ["schemaVersion"] = SchemaVersion };
        var array = new JsonArray();
        foreach (var project in content.Projects.OrderBy(p => p.Order))
        {
            array.Add(WriteProject(project));
        }

        foreach (var raw in content.UnrecognizedEntries)
        {
            array.Add(raw.DeepClone());
        }

        root["projects"] = array;
        CopyExtra(content.RootExtra, root, s_knownRootKeys);
        return root.ToJsonString(s_writeOptions);
    }

    /// <summary>Normalizes a root path for storage and comparison ("D:\a\" → "D:\a", "E:\" stays).</summary>
    public static string NormalizeRoot(string root)
    {
        var full = Path.GetFullPath(root.Trim().Trim('"'));
        return Path.GetPathRoot(full) == full ? full : Path.TrimEndingDirectorySeparator(full);
    }

    private static ProjectsFileContent ParseCore(string json)
    {
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        if (root is not JsonObject obj)
        {
            throw new ProjectsFileException("projects.json must contain a JSON object.");
        }

        var versionNode = obj["schemaVersion"];
        var version = SchemaVersion;
        if (versionNode is not null && !(versionNode is JsonValue v && v.TryGetValue(out version)))
        {
            throw new ProjectsFileException("projects.json 'schemaVersion' must be an integer.");
        }

        if (version > SchemaVersion)
        {
            throw new ProjectsFileException(
                $"projects.json schema {version} is newer than supported ({SchemaVersion}).",
                isNewerSchema: true);
        }

        var projects = new List<ProjectDefinition>();
        var unrecognized = new List<JsonNode>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        switch (obj["projects"])
        {
            case null:
                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    // Duplicate ids are kept verbatim but not loaded, so nothing is lost on save.
                    if (item is JsonObject entry && TryReadProject(entry, version) is { } definition && seenIds.Add(definition.Id))
                    {
                        projects.Add(definition);
                    }
                    else if (item is not null)
                    {
                        unrecognized.Add(item.DeepClone());
                    }
                }

                break;
            default:
                throw new ProjectsFileException("projects.json 'projects' must be an array.");
        }

        return new ProjectsFileContent(projects, unrecognized, ExtractExtra(obj, s_knownRootKeys)) { FileVersion = version };
    }

    private static ProjectDefinition? TryReadProject(JsonObject entry, int fileVersion)
    {
        if (!TryGetString(entry, "id", required: true, out var id) || string.IsNullOrWhiteSpace(id)
            || !TryGetString(entry, "kind", required: true, out var kindText)
            || !TryGetString(entry, "color", required: false, out var color)
            || !TryGetLegacyFilter(entry, out var filter)
            || !TryGetInt(entry, "order", out var order)
            || !TryGetBool(entry, "hidden", out var hidden)
            || !TryGetExcluded(entry, out var excluded)
            || !TryGetStringList(entry, "visibleItems", out var visible)
            || !TryGetVisibility(entry, out var visibility)
            || !TryGetView(entry, out var view))
        {
            return null;
        }

        // v1 entries (and v1 entries a later version kept verbatim) carry "filter"/"visibleItems" instead of "visibility".
        if (visibility is null && (fileVersion < 2 || entry.ContainsKey("filter") || entry.ContainsKey("visibleItems")))
        {
            visibility = MigrateLegacy(filter ?? "all", visible);
        }

        var extra = ExtractExtra(entry, s_knownProjectKeys);
        switch (kindText)
        {
            case "builtin":
                if (!TryGetString(entry, "builtinKey", required: true, out var key)
                    || BuiltinProjects.FromKey(key) is not { } builtin
                    || id != BuiltinProjects.IdOf(builtin))
                {
                    return null;
                }

                return new ProjectDefinition
                {
                    Id = id,
                    Kind = ProjectKind.Builtin,
                    Builtin = builtin,
                    Name = BuiltinProjects.NameOf(builtin),
                    Color = color,
                    Order = order,
                    Hidden = hidden,
                    ExcludedFolders = excluded,
                    Visibility = visibility ?? VisibilitySettings.Default,
                    View = view ?? ProjectViewSettings.Default,
                    Extra = extra,
                };

            case "folder":
                // Built-in ids are reserved; a folder entry using one is preserved but not loaded.
                if (id.StartsWith(BuiltinIdPrefix, StringComparison.OrdinalIgnoreCase)
                    || !TryGetString(entry, "name", required: true, out var name) || string.IsNullOrWhiteSpace(name)
                    || !TryGetString(entry, "root", required: true, out var root) || string.IsNullOrWhiteSpace(root)
                    || !Path.IsPathFullyQualified(root!))
                {
                    return null;
                }

                string normalizedRoot;
                try
                {
                    normalizedRoot = NormalizeRoot(root!);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    return null;
                }

                return new ProjectDefinition
                {
                    Id = id,
                    Kind = ProjectKind.Folder,
                    Name = name!,
                    Root = normalizedRoot,
                    Color = color,
                    Order = order,
                    Hidden = hidden,
                    ExcludedFolders = excluded,
                    Visibility = visibility ?? VisibilitySettings.Default,
                    View = view ?? ProjectViewSettings.Default,
                    Extra = extra,
                };

            default:
                return null;
        }
    }

    private static JsonObject WriteProject(ProjectDefinition project)
    {
        var obj = new JsonObject
        {
            ["id"] = project.Id,
            ["kind"] = project.IsBuiltin ? "builtin" : "folder",
        };

        if (project.IsBuiltin)
        {
            obj["builtinKey"] = BuiltinProjects.KeyOf(project.Builtin!.Value);
        }
        else
        {
            obj["name"] = project.Name;
            obj["root"] = project.Root;
        }

        if (project.Color is not null)
        {
            obj["color"] = project.Color;
        }

        obj["order"] = project.Order;
        obj["hidden"] = project.Hidden;
        obj["excludedFolders"] = project.ExcludedFolders is null
            ? null
            : new JsonArray(project.ExcludedFolders.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray());

        obj["visibility"] = WriteVisibility(project.Visibility);
        obj["view"] = WriteView(project.View);

        CopyExtra(project.Extra, obj, s_knownProjectKeys);
        return obj;
    }

    /// <summary>Copies extension fields, never letting them override the validated, known fields.</summary>
    private static void CopyExtra(JsonObject? extra, JsonObject target, HashSet<string> knownKeys)
    {
        if (extra is null)
        {
            return;
        }

        foreach (var (key, value) in extra)
        {
            if (!knownKeys.Contains(key))
            {
                target[key] = value?.DeepClone();
            }
        }
    }

    // Each TryGet returns false when the field is present but has an unsupported type or value.
    private static bool TryGetString(JsonObject obj, string key, bool required, out string? value)
    {
        value = null;
        var node = obj[key];
        if (node is null)
        {
            return !required;
        }

        return node is JsonValue v && v.TryGetValue(out value);
    }

    /// <summary>The v1 "filter" value ("all", "configDocs" or null when absent).</summary>
    private static bool TryGetLegacyFilter(JsonObject obj, out string? filter) =>
        TryGetString(obj, "filter", required: false, out filter) && filter is null or "all" or "configDocs";

    /// <summary>
    /// v1 → v2: "all" shows every file; "configDocs" becomes an extension list. "visibleItems" (show these, everything
    /// below checked folders, and the folders on the way) becomes: root excluded, each item included.
    /// </summary>
    internal static VisibilitySettings MigrateLegacy(string filter, IReadOnlyList<string>? visibleItems)
    {
        var rules = new Dictionary<string, VisibilityRule>(RelativeKey.Comparer);
        var items = (visibleItems ?? []).Select(RelativeKey.Normalize).Where(i => i.Length > 0).Distinct(RelativeKey.Comparer).ToList();
        if (items.Count > 0)
        {
            rules[string.Empty] = VisibilityRule.Exclude;
            foreach (var item in items.Where(i => !items.Any(other => RelativeKey.IsInside(i, other))))
            {
                rules[item] = VisibilityRule.Include;
            }
        }

        return new VisibilitySettings
        {
            Rules = rules,
            ShowAllFiles = filter != "configDocs",
            DefaultExtensions = filter == "configDocs" ? LegacyConfigDocsExtensions : null,
        };
    }

    private static bool TryGetVisibility(JsonObject obj, out VisibilitySettings? visibility)
    {
        visibility = null;
        switch (obj["visibility"])
        {
            case null:
                return true;
            case JsonObject v:
                // An extension that cannot be normalized makes the entry invalid (kept verbatim) instead of being dropped.
                if (!TryGetBool(v, "showAllFiles", out var showAll) || !TryGetStringList(v, "defaultExtensions", out var extensions)
                    || (extensions is not null && extensions.Any(e => ExtensionRules.Normalize(e) is null)))
                {
                    return false;
                }

                var rules = new Dictionary<string, VisibilityRule>(RelativeKey.Comparer);
                switch (v["rules"])
                {
                    case null:
                        break;
                    case JsonObject map:
                        foreach (var (key, value) in map)
                        {
                            if (value is not JsonValue rv || !rv.TryGetValue<string>(out var text) || text is not ("include" or "exclude"))
                            {
                                return false;
                            }

                            // Keys that differ only by case collapse into one (the last wins), like Windows paths.
                            rules[RelativeKey.Normalize(key)] = text == "include" ? VisibilityRule.Include : VisibilityRule.Exclude;
                        }

                        break;
                    default:
                        return false;
                }

                visibility = new VisibilitySettings
                {
                    Rules = rules,
                    ShowAllFiles = showAll,
                    DefaultExtensions = extensions is null ? null : ExtensionRules.NormalizeAll(extensions),
                    Extra = ExtractExtra(v, s_knownVisibilityKeys),
                };
                return true;
            default:
                return false;
        }
    }

    private static bool TryGetView(JsonObject obj, out ProjectViewSettings? view)
    {
        view = null;
        switch (obj["view"])
        {
            case null:
                return true;
            case JsonObject v:
                if (!TryGetString(v, "mode", required: false, out var mode) || mode is not (null or "tree" or "compact" or "flat")
                    || !TryGetString(v, "flatSort", required: false, out var sort) || sort is not (null or "name" or "custom")
                    || !TryGetStringList(v, "flatOrder", out var order)
                    || !TryGetStringList(v, "pins", out var pins))
                {
                    return false;
                }

                view = new ProjectViewSettings
                {
                    Mode = mode switch { "tree" => ExplorerViewMode.Tree, "flat" => ExplorerViewMode.Flat, _ => ExplorerViewMode.Compact },
                    FlatSort = sort == "custom" ? FlatSort.Custom : FlatSort.Name,
                    FlatOrder = (order ?? []).Select(RelativeKey.Normalize).Where(k => k.Length > 0).Distinct(RelativeKey.Comparer).ToList(),
                    Pins = (pins ?? []).Select(RelativeKey.Normalize).Where(k => k.Length > 0).Distinct(RelativeKey.Comparer).ToList(),
                    Extra = ExtractExtra(v, s_knownViewKeys),
                };
                return true;
            default:
                return false;
        }
    }

    private static JsonObject WriteVisibility(VisibilitySettings visibility)
    {
        var rules = new JsonObject();
        foreach (var (key, value) in visibility.Rules.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Key, StringComparer.Ordinal))
        {
            rules[RelativeKey.ToStorage(key)] = value == VisibilityRule.Include ? "include" : "exclude";
        }

        var obj = new JsonObject
        {
            ["defaultExtensions"] = visibility.DefaultExtensions is null ? null : Strings(visibility.DefaultExtensions),
            ["showAllFiles"] = visibility.ShowAllFiles,
            ["rules"] = rules,
        };
        CopyExtra(visibility.Extra, obj, s_knownVisibilityKeys);
        return obj;
    }

    private static JsonObject WriteView(ProjectViewSettings view)
    {
        var obj = new JsonObject
        {
            ["mode"] = view.Mode switch { ExplorerViewMode.Tree => "tree", ExplorerViewMode.Flat => "flat", _ => "compact" },
            ["flatSort"] = view.FlatSort == FlatSort.Custom ? "custom" : "name",
            ["flatOrder"] = Strings(view.FlatOrder.Select(RelativeKey.ToStorage)),
            ["pins"] = Strings(view.Pins.Select(RelativeKey.ToStorage)),
        };
        CopyExtra(view.Extra, obj, s_knownViewKeys);
        return obj;
    }

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray());

    private static bool TryGetInt(JsonObject obj, string key, out int value)
    {
        value = int.MaxValue;
        var node = obj[key];
        return node is null || (node is JsonValue v && v.TryGetValue(out value));
    }

    private static bool TryGetBool(JsonObject obj, string key, out bool value)
    {
        value = false;
        var node = obj[key];
        return node is null || (node is JsonValue v && v.TryGetValue(out value));
    }

    private static bool TryGetExcluded(JsonObject obj, out IReadOnlyList<string>? excluded) =>
        TryGetStringList(obj, "excludedFolders", out excluded);

    private static bool TryGetStringList(JsonObject obj, string key, out IReadOnlyList<string>? excluded)
    {
        excluded = null;
        switch (obj[key])
        {
            case null:
                return true;
            case JsonArray array:
                var list = new List<string>(array.Count);
                foreach (var item in array)
                {
                    if (item is not JsonValue v || !v.TryGetValue<string>(out var s))
                    {
                        return false;
                    }

                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        list.Add(s);
                    }
                }

                excluded = list;
                return true;
            default:
                return false;
        }
    }

    private static JsonObject? ExtractExtra(JsonObject source, HashSet<string> knownKeys)
    {
        JsonObject? extra = null;
        foreach (var (key, value) in source)
        {
            if (!knownKeys.Contains(key))
            {
                extra ??= [];
                extra[key] = value?.DeepClone();
            }
        }

        return extra;
    }
}

public sealed class ProjectsFileException : Exception
{
    public ProjectsFileException(string message, bool isNewerSchema = false)
        : base(message)
    {
        IsNewerSchema = isNewerSchema;
    }

    public ProjectsFileException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The file was written by a newer DotDeck; it must not be overwritten.</summary>
    public bool IsNewerSchema { get; }
}
