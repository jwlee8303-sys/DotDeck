using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text.Json;
using DotDeck.Core.Logging;

namespace DotDeck.Core.Icons;

/// <summary>Inputs of a resolver. Null JSON means the layer is absent.</summary>
/// <param name="AvailableKeys">Icon keys the app can draw (Material keys plus DotDeck's own); user and DotDeck mappings may only use these.</param>
/// <param name="UserSource">Shown in warnings about the user layer (usually the associations.json path).</param>
public sealed record IconSources(
    string? MaterialJson,
    string? DotDeckJson,
    string? UserJson,
    IReadOnlySet<string> AvailableKeys,
    string UserSource = "associations.json");

/// <summary>
/// Resolves the icon key of a file or folder. UI-free and immutable: a new mapping means a new resolver (swap the
/// instance), so lookups never race with a reload. Order:
/// <list type="number">
/// <item>user associations (file name, then extensions longest first / folder name)</item>
/// <item>DotDeck rules (tool-scoped rules when a scope is given, then distinctive names)</item>
/// <item>Material (light overrides first in the light theme): file name, extensions longest first
///   ("a.d.ts" → "d.ts" → "ts"), language of the extension, default file / folder name, default folder</item>
/// </list>
/// Names compare case-insensitively. A plain dotfile (".gitignore") is matched by name only.
/// </summary>
public sealed class FileIconResolver
{
    private const int MaxCacheEntries = 50_000;

    /// <summary>Warnings logged per layer; the rest are counted (a broken file must not flood the output pane).</summary>
    private const int MaxWarningsPerLayer = 20;

    /// <summary>Extension → VS Code language id, for extensions Material maps only through languageIds.</summary>
    private static readonly Dictionary<string, string> s_languages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["md"] = "markdown", ["markdown"] = "markdown", ["json"] = "json", ["jsonc"] = "jsonc", ["yml"] = "yaml", ["yaml"] = "yaml",
        ["toml"] = "toml", ["xml"] = "xml", ["xaml"] = "xml", ["ini"] = "ini", ["cfg"] = "ini", ["conf"] = "ini",
        ["ps1"] = "powershell", ["psm1"] = "powershell", ["psd1"] = "powershell", ["cs"] = "csharp", ["py"] = "python",
        ["js"] = "javascript", ["ts"] = "typescript", ["html"] = "html", ["css"] = "css", ["sql"] = "sql",
        ["sh"] = "shellscript", ["bat"] = "bat", ["cmd"] = "bat", ["txt"] = "plaintext",
    };

    private readonly ConcurrentDictionary<CacheKey, string> _cache = new();

    public FileIconResolver(IconManifest material, DotDeckIconRules dotDeck, IconAssociations user, IReadOnlySet<string> availableKeys)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(dotDeck);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(availableKeys);

        // Snapshots: cached results stay valid because nothing the caller still holds can change the mapping.
        Material = material.Snapshot();
        DotDeck = dotDeck.Snapshot();
        User = user.Snapshot();
        AvailableKeys = availableKeys.ToFrozenSet(StringComparer.Ordinal);
    }

    public IconManifest Material { get; }

    public DotDeckIconRules DotDeck { get; }

    public IconAssociations User { get; }

    public IReadOnlySet<string> AvailableKeys { get; }

    /// <summary>
    /// Builds a resolver from JSON. A layer that cannot be read is skipped with a warning (the next layer decides);
    /// invalid entries are dropped with one warning each. Never throws for bad JSON.
    /// </summary>
    public static FileIconResolver Create(IconSources sources, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(log);

        T Layer<T>(string? json, string source, T empty, Func<string, ICollection<string>, T> parse)
        {
            if (json is null)
            {
                return empty;
            }

            var warnings = new List<string>();
            try
            {
                var result = parse(json, warnings);
                foreach (var w in warnings.Take(MaxWarningsPerLayer))
                {
                    log.Warning($"파일 아이콘({source}): {w}");
                }

                if (warnings.Count > MaxWarningsPerLayer)
                {
                    log.Warning($"파일 아이콘({source}): 그 밖의 경고 {warnings.Count - MaxWarningsPerLayer}건");
                }

                return result;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                log.Warning($"파일 아이콘({source})을 읽지 못해 무시합니다: {ex.Message}");
                return empty;
            }
        }

        var material = Layer(sources.MaterialJson, "material-icons.json", IconManifest.Empty, IconManifest.Parse);
        var dotDeck = Layer(sources.DotDeckJson, "dotdeck-icons.json", DotDeckIconRules.Empty, (j, w) => DotDeckIconRules.Parse(j, sources.AvailableKeys, w));
        var user = Layer(sources.UserJson, sources.UserSource, IconAssociations.Empty, (j, w) => IconAssociations.Parse(j, sources.AvailableKeys, w));
        return new FileIconResolver(material, dotDeck, user, sources.AvailableKeys);
    }

    /// <param name="name">File or folder name (not a path).</param>
    /// <param name="scope">Position inside a tool's configuration folder, if any (see <see cref="IconScopeResolver"/>).</param>
    public string Resolve(string name, bool isFolder, bool isExpanded, IconTheme theme, IconScope? scope = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        var key = new CacheKey(name.ToUpperInvariant(), isFolder, isFolder && isExpanded, theme, scope?.Tool, scope?.RelativePath.ToUpperInvariant());
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var result = isFolder ? ResolveFolder(name, isExpanded, theme, scope) : ResolveFile(name, theme, scope);

        // A full cache stops growing instead of being cleared, so concurrent lookups never undo each other's work.
        if (_cache.Count < MaxCacheEntries)
        {
            _cache.TryAdd(key, result);
        }

        return result;
    }

    private string ResolveFile(string name, IconTheme theme, IconScope? scope)
    {
        var extensions = ExtensionsOf(name);
        if (User.FileNames.TryGetValue(name, out var user) || TryExtensions(User.FileExtensions, extensions, out user))
        {
            return user;
        }

        // Distinctive file names (AGENTS.md, SKILL.md) keep their icon even where a tool folder rule such as
        // "rules/**/*.md" also matches.
        if (DotDeck.FileNames.TryGetValue(name, out var distinctive))
        {
            return distinctive;
        }

        if (scope is not null && DotDeck.MatchScoped(scope, isFolder: false) is { } scoped)
        {
            return scoped;
        }

        var light = theme == IconTheme.Light ? Material.Light : null;
        if ((light is not null && light.FileNames.TryGetValue(name, out var m)) || Material.Dark.FileNames.TryGetValue(name, out m))
        {
            return m;
        }

        foreach (var extension in extensions)
        {
            if ((light is not null && light.FileExtensions.TryGetValue(extension, out m)) || Material.Dark.FileExtensions.TryGetValue(extension, out m))
            {
                return m;
            }
        }

        if (extensions.Count > 0 && s_languages.TryGetValue(extensions[^1], out var language)
            && ((light is not null && light.LanguageIds.TryGetValue(language, out m)) || Material.Dark.LanguageIds.TryGetValue(language, out m)))
        {
            return m;
        }

        return (light is not null ? Material.LightFile : null) ?? Material.File;
    }

    private string ResolveFolder(string name, bool isExpanded, IconTheme theme, IconScope? scope)
    {
        if (User.FolderNames.TryGetValue(name, out var user))
        {
            return isExpanded ? Expanded(user) : user;
        }

        if ((scope is not null && DotDeck.MatchScoped(scope, isFolder: true) is { } scoped) || DotDeck.FolderNames.TryGetValue(name, out scoped))
        {
            return isExpanded ? Expanded(scoped) : scoped;
        }

        var light = theme == IconTheme.Light ? Material.Light : null;
        var map = isExpanded ? Material.Dark.FolderNamesExpanded : Material.Dark.FolderNames;
        var lightMap = isExpanded ? light?.FolderNamesExpanded : light?.FolderNames;
        if ((lightMap is not null && lightMap.TryGetValue(name, out var m)) || map.TryGetValue(name, out m))
        {
            return m;
        }

        // A folder Material knows only closed still opens with its "-open" variant.
        if (isExpanded && ((light is not null && light.FolderNames.TryGetValue(name, out var closed)) || Material.Dark.FolderNames.TryGetValue(name, out closed)))
        {
            return Expanded(closed);
        }

        return isExpanded
            ? (light is not null ? Material.LightFolderExpanded : null) ?? Material.FolderExpanded
            : (light is not null ? Material.LightFolder : null) ?? Material.Folder;
    }

    private string Expanded(string folderKey) => AvailableKeys.Contains(folderKey + "-open") ? folderKey + "-open" : folderKey;

    private static bool TryExtensions(IReadOnlyDictionary<string, string> map, IReadOnlyList<string> extensions, out string value)
    {
        foreach (var extension in extensions)
        {
            if (map.TryGetValue(extension, out value!))
            {
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    /// <summary>Extension candidates, longest first: "a.spec.ts" → ["spec.ts", "ts"]. A plain dotfile has none.</summary>
    internal static IReadOnlyList<string> ExtensionsOf(string name)
    {
        // A leading dot is part of the name, not an extension: ".eslintrc.json" → ["json"], ".gitignore" → [].
        var start = name.Length > 0 && name[0] == '.' ? name.IndexOf('.', 1) : name.IndexOf('.');
        if (start < 0)
        {
            return [];
        }

        var result = new List<string>();
        for (var i = start; i >= 0 && i < name.Length - 1; i = name.IndexOf('.', i + 1))
        {
            result.Add(name[(i + 1)..]);
        }

        return result;
    }

    private readonly record struct CacheKey(string Name, bool IsFolder, bool IsExpanded, IconTheme Theme, IconTool? Tool, string? RelativePath);
}
