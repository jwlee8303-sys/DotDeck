using System.Collections.Frozen;
using System.Text.Json;

namespace DotDeck.Core.Icons;

/// <summary>Name → icon key maps of one Material section (case-insensitive).</summary>
public sealed record IconMappings
{
    // Declared before Empty: static initializers run in order and Empty's properties read this.
    internal static IReadOnlyDictionary<string, string> EmptyMap { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase).ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static IconMappings Empty { get; } = new();

    public IReadOnlyDictionary<string, string> FileExtensions { get; init; } = EmptyMap;

    public IReadOnlyDictionary<string, string> FileNames { get; init; } = EmptyMap;

    public IReadOnlyDictionary<string, string> FolderNames { get; init; } = EmptyMap;

    public IReadOnlyDictionary<string, string> FolderNamesExpanded { get; init; } = EmptyMap;

    public IReadOnlyDictionary<string, string> LanguageIds { get; init; } = EmptyMap;

    /// <summary>An immutable copy with case-insensitive keys, so later changes to the source maps cannot leak in.</summary>
    internal IconMappings Snapshot() => new()
    {
        FileExtensions = IconJson.Freeze(FileExtensions),
        FileNames = IconJson.Freeze(FileNames),
        FolderNames = IconJson.Freeze(FolderNames),
        FolderNamesExpanded = IconJson.Freeze(FolderNamesExpanded),
        LanguageIds = IconJson.Freeze(LanguageIds),
    };
}

/// <summary>
/// The Material Icon Theme mapping (<c>material-icons.json</c>, VS Code icon theme format). Only the parts DotDeck
/// uses are read: icon definitions, the default icons and the name maps. <see cref="Light"/> holds the light-theme
/// overrides.
/// </summary>
public sealed record IconManifest
{
    public const string DefaultFile = "file";
    public const string DefaultFolder = "folder";
    public const string DefaultFolderExpanded = "folder-open";

    public static IconManifest Empty { get; } = new();

    /// <summary>Every icon key the manifest defines.</summary>
    public IReadOnlySet<string> Definitions { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public string File { get; init; } = DefaultFile;

    public string Folder { get; init; } = DefaultFolder;

    public string FolderExpanded { get; init; } = DefaultFolderExpanded;

    /// <summary>Light-theme default icons (null: same as dark).</summary>
    public string? LightFile { get; init; }

    public string? LightFolder { get; init; }

    public string? LightFolderExpanded { get; init; }

    public IconMappings Dark { get; init; } = IconMappings.Empty;

    public IconMappings Light { get; init; } = IconMappings.Empty;

    /// <summary>An immutable copy (see <see cref="IconMappings.Snapshot"/>).</summary>
    internal IconManifest Snapshot() => this with
    {
        Definitions = Definitions.ToFrozenSet(StringComparer.Ordinal),
        Dark = Dark.Snapshot(),
        Light = Light.Snapshot(),
    };

    /// <summary>Parses the manifest. Invalid entries are skipped and reported through <paramref name="warnings"/>.</summary>
    /// <exception cref="JsonException">The text is not a JSON object.</exception>
    public static IconManifest Parse(string json, ICollection<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        using var doc = JsonDocument.Parse(json, IconJson.Options);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("material-icons.json must contain a JSON object.");
        }

        IconJson.WarnDuplicates(root, warnings);

        var definitions = new HashSet<string>(StringComparer.Ordinal);
        if (IconJson.TryFirst(root, "iconDefinitions", out var defs) && defs.ValueKind == JsonValueKind.Object)
        {
            foreach (var def in defs.EnumerateObject())
            {
                if (IconJson.TryName(def, out var name))
                {
                    definitions.Add(name);
                }
            }
        }
        else
        {
            warnings.Add("'iconDefinitions'가 없어 기본 아이콘만 사용합니다.");
        }

        var skipped = 0;
        IconMappings Mappings(JsonElement section) => new()
        {
            FileExtensions = IconJson.Map(section, "fileExtensions", definitions, ref skipped, IconJson.NormalizeExtension),
            FileNames = IconJson.Map(section, "fileNames", definitions, ref skipped),
            FolderNames = IconJson.Map(section, "folderNames", definitions, ref skipped),
            FolderNamesExpanded = IconJson.Map(section, "folderNamesExpanded", definitions, ref skipped),
            LanguageIds = IconJson.Map(section, "languageIds", definitions, ref skipped),
        };

        var hasLight = IconJson.TryFirst(root, "light", out var light) && light.ValueKind == JsonValueKind.Object;
        var manifest = new IconManifest
        {
            Definitions = definitions,
            File = IconJson.Key(root, "file", definitions) ?? DefaultFile,
            Folder = IconJson.Key(root, "folder", definitions) ?? DefaultFolder,
            FolderExpanded = IconJson.Key(root, "folderExpanded", definitions) ?? DefaultFolderExpanded,
            Dark = Mappings(root),
            Light = hasLight ? Mappings(light) : IconMappings.Empty,
            LightFile = hasLight ? IconJson.Key(light, "file", definitions) : null,
            LightFolder = hasLight ? IconJson.Key(light, "folder", definitions) : null,
            LightFolderExpanded = hasLight ? IconJson.Key(light, "folderExpanded", definitions) : null,
        };

        if (skipped > 0)
        {
            warnings.Add($"Material 아이콘 매핑에서 정의되지 않은 아이콘을 가리키는 항목 {skipped}개를 무시했습니다.");
        }

        return manifest;
    }
}

internal static class IconJson
{
    public static JsonDocumentOptions Options { get; } = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static string? Key(JsonElement root, string name, IReadOnlySet<string> definitions) =>
        TryFirst(root, name, out var v) && TryString(v, out var s) && definitions.Contains(s) ? s : null;

    /// <summary>The string value; false for other kinds and for strings that cannot be decoded (lone surrogates).</summary>
    public static bool TryString(JsonElement value, out string text)
    {
        text = string.Empty;
        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        try
        {
            text = value.GetString() ?? string.Empty;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>The property name; false when it cannot be decoded (lone surrogates).</summary>
    public static bool TryName(JsonProperty property, out string name)
    {
        try
        {
            name = property.Name;
            return true;
        }
        catch (InvalidOperationException)
        {
            name = string.Empty;
            return false;
        }
    }

    /// <summary>Warns about sections that appear more than once; the first one is used (see <see cref="TryFirst"/>).</summary>
    public static void WarnDuplicates(JsonElement root, ICollection<string> warnings)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in root.EnumerateObject())
        {
            if (TryName(p, out var name) && !seen.Add(name))
            {
                warnings.Add($"'{Short(name)}' 항목이 두 번 이상 있습니다. 첫 번째만 사용합니다.");
            }
        }
    }

    /// <summary>The first property with this name (which duplicate JsonElement.TryGetProperty returns is unspecified).</summary>
    public static bool TryFirst(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in obj.EnumerateObject())
            {
                if (p.NameEquals(name))
                {
                    value = p.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    /// <summary>Keeps warnings readable when a file contains a huge name.</summary>
    public static string Short(string text) => text.Length <= 80 ? text : text[..80] + "…";

    /// <summary>Immutable, case-insensitive copy (the first of keys differing only in case wins).</summary>
    public static IReadOnlyDictionary<string, string> Freeze(IReadOnlyDictionary<string, string> map)
    {
        if (map is FrozenDictionary<string, string> frozen && ReferenceEquals(frozen.Comparer, StringComparer.OrdinalIgnoreCase))
        {
            return frozen;
        }

        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in map)
        {
            copy.TryAdd(key, value);
        }

        return copy.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Reads a string→string object; entries whose value is not a defined key are counted and skipped.</summary>
    public static IReadOnlyDictionary<string, string> Map(
        JsonElement section, string name, IReadOnlySet<string> definitions, ref int skipped, Func<string, string>? normalizeKey = null)
    {
        if (!TryFirst(section, name, out var obj) || obj.ValueKind != JsonValueKind.Object)
        {
            return IconMappings.EmptyMap;
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in obj.EnumerateObject())
        {
            if (!TryName(p, out var rawKey) || !TryString(p.Value, out var value) || !definitions.Contains(value))
            {
                skipped++;
                continue;
            }

            var key = normalizeKey is null ? rawKey : normalizeKey(rawKey);
            if (key.Length == 0)
            {
                skipped++;
                continue;
            }

            map.TryAdd(key, value);
        }

        return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>"*.ts", ".ts" and "ts" all mean the extension "ts".</summary>
    public static string NormalizeExtension(string extension)
    {
        var e = extension.Trim();
        if (e.StartsWith("*.", StringComparison.Ordinal))
        {
            e = e[2..];
        }

        return e.TrimStart('.');
    }
}
