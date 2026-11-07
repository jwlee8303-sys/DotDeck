using System.Text.Json;
using System.Text.Json.Nodes;

namespace DotDeck.Core.Projects;

/// <summary>Parsed projects.json content.</summary>
public sealed record ProjectsFileContent(
    IReadOnlyList<ProjectDefinition> Projects,
    IReadOnlyList<JsonNode> UnrecognizedEntries,
    JsonObject? RootExtra);

/// <summary>
/// Reads and writes projects.json (schema v1). Unknown fields — at the root and per project — are kept.
/// An entry is loaded only if every known field is valid; otherwise the whole entry is written back verbatim,
/// so values this version does not understand (e.g. a future filter mode) are never rewritten.
/// </summary>
public static class ProjectsFileSerializer
{
    public const int SchemaVersion = 1;
    public const string BuiltinIdPrefix = "builtin.";

    private static readonly HashSet<string> s_knownRootKeys = ["schemaVersion", "projects"];

    private static readonly HashSet<string> s_knownProjectKeys =
        ["id", "kind", "builtinKey", "name", "root", "color", "filter", "order", "hidden", "excludedFolders"];

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
                    if (item is JsonObject entry && TryReadProject(entry) is { } definition && seenIds.Add(definition.Id))
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

        return new ProjectsFileContent(projects, unrecognized, ExtractExtra(obj, s_knownRootKeys));
    }

    private static ProjectDefinition? TryReadProject(JsonObject entry)
    {
        if (!TryGetString(entry, "id", required: true, out var id) || string.IsNullOrWhiteSpace(id)
            || !TryGetString(entry, "kind", required: true, out var kindText)
            || !TryGetString(entry, "color", required: false, out var color)
            || !TryGetFilter(entry, out var filter)
            || !TryGetInt(entry, "order", out var order)
            || !TryGetBool(entry, "hidden", out var hidden)
            || !TryGetExcluded(entry, out var excluded))
        {
            return null;
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
                    Filter = filter,
                    Order = order,
                    Hidden = hidden,
                    ExcludedFolders = excluded,
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
                    Filter = filter,
                    Order = order,
                    Hidden = hidden,
                    ExcludedFolders = excluded,
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

        obj["filter"] = project.Filter == ExplorerFilterMode.ConfigDocs ? "configDocs" : "all";
        obj["order"] = project.Order;
        obj["hidden"] = project.Hidden;
        obj["excludedFolders"] = project.ExcludedFolders is null
            ? null
            : new JsonArray(project.ExcludedFolders.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray());

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

    private static bool TryGetFilter(JsonObject obj, out ExplorerFilterMode filter)
    {
        filter = ExplorerFilterMode.All;
        if (!TryGetString(obj, "filter", required: false, out var text))
        {
            return false;
        }

        switch (text)
        {
            case null or "all":
                return true;
            case "configDocs":
                filter = ExplorerFilterMode.ConfigDocs;
                return true;
            default:
                return false;
        }
    }

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

    private static bool TryGetExcluded(JsonObject obj, out IReadOnlyList<string>? excluded)
    {
        excluded = null;
        switch (obj["excludedFolders"])
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
