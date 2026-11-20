using System.Collections.Frozen;
using System.Text.Json;

namespace DotDeck.Core.Icons;

/// <summary>
/// User icon associations (<c>%APPDATA%\DotDeck\icons\associations.json</c>), like VS Code's files.associations:
/// <code>{ "fileExtensions": { "cfg": "settings" }, "fileNames": { "my-tool.conf": "settings" }, "folderNames": { "prompts": "folder-docs" } }</code>
/// Values must be existing icon keys. Folder icons switch to "&lt;key&gt;-open" when expanded if that icon exists.
/// </summary>
public sealed record IconAssociations
{
    /// <summary>Reserved prefix for user SVG icons (not supported yet; such values are ignored with a warning).</summary>
    public const string CustomPrefix = "custom:";

    private static readonly HashSet<string> s_sections = ["fileExtensions", "fileNames", "folderNames"];

    public static IconAssociations Empty { get; } = new();

    public IReadOnlyDictionary<string, string> FileExtensions { get; init; } = IconMappings.EmptyMap;

    public IReadOnlyDictionary<string, string> FileNames { get; init; } = IconMappings.EmptyMap;

    public IReadOnlyDictionary<string, string> FolderNames { get; init; } = IconMappings.EmptyMap;

    /// <summary>An immutable copy with case-insensitive keys.</summary>
    internal IconAssociations Snapshot() => new()
    {
        FileExtensions = IconJson.Freeze(FileExtensions),
        FileNames = IconJson.Freeze(FileNames),
        FolderNames = IconJson.Freeze(FolderNames),
    };

    public bool IsEmpty => FileExtensions.Count == 0 && FileNames.Count == 0 && FolderNames.Count == 0;

    /// <exception cref="JsonException">The text is not a JSON object.</exception>
    public static IconAssociations Parse(string json, IReadOnlySet<string> availableKeys, ICollection<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(availableKeys);
        ArgumentNullException.ThrowIfNull(warnings);
        using var doc = JsonDocument.Parse(json, IconJson.Options);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("associations.json must contain a JSON object.");
        }

        IconJson.WarnDuplicates(doc.RootElement, warnings);
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (!IconJson.TryName(p, out var section) || (!s_sections.Contains(section) && !section.StartsWith('$')))
            {
                warnings.Add($"알 수 없는 항목 '{IconJson.Short(section)}'을 무시합니다.");
            }
        }

        IReadOnlyDictionary<string, string> Section(string name, Func<string, string>? normalize = null)
        {
            if (!IconJson.TryFirst(doc.RootElement, name, out var obj))
            {
                return IconMappings.EmptyMap;
            }

            if (obj.ValueKind != JsonValueKind.Object)
            {
                warnings.Add($"'{name}'은 객체여야 합니다. 무시합니다.");
                return IconMappings.EmptyMap;
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in obj.EnumerateObject())
            {
                if (!IconJson.TryName(p, out var rawKey))
                {
                    warnings.Add($"'{name}'에 읽을 수 없는 이름이 있어 무시합니다.");
                    continue;
                }

                var key = normalize is null ? rawKey.Trim() : normalize(rawKey);
                var shown = IconJson.Short(rawKey);
                if (key.Length == 0 || !IconJson.TryString(p.Value, out var raw))
                {
                    warnings.Add($"'{name}'의 '{shown}' 값이 문자열이 아니어서 무시합니다.");
                    continue;
                }

                var value = raw.Trim();
                if (value.StartsWith(CustomPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add($"'{name}'의 '{shown}': 사용자 아이콘({CustomPrefix})은 아직 지원하지 않아 무시합니다.");
                }
                else if (!availableKeys.Contains(value))
                {
                    warnings.Add($"'{name}'의 '{shown}': 아이콘 '{IconJson.Short(value)}'이 없어 무시합니다.");
                }
                else
                {
                    map[key] = value;
                }
            }

            return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        }

        return new IconAssociations
        {
            FileExtensions = Section("fileExtensions", IconJson.NormalizeExtension),
            FileNames = Section("fileNames"),
            FolderNames = Section("folderNames"),
        };
    }
}
