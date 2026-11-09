using System.Text.Json;
using DotDeck.Core.Catalogs;

namespace DotDeck.Core.AiCli.Snapshots;

/// <summary>
/// Files a CLI writes for itself in its own config folder (state, history, caches, sync folders): CLI system records.
/// Inside a session scope rooted at that folder they are left out of the start index, the start capture and the change
/// summary, so they are neither backed up nor offered for review. A file a Claude tool wrote is still a user change:
/// the guard captures it before the write, and a captured path is always summarized (DESIGN "CLI 시스템 기록").
/// Entries are relative to the config folder: <c>"/.claude.json"</c> is a file, <c>"/backups/"</c> (trailing
/// separator) a folder with everything below it. Matching is case-insensitive.
/// </summary>
public sealed class SystemRecordPolicy
{
    public const string ClaudeResource = "Claude/system-records.json";
    public const string CodexResource = "Codex/system-records.json";

    private static readonly Lazy<SystemRecordPolicy> s_claude = new(() => Parse(CatalogResources.ReadBuiltIn(ClaudeResource)));
    private static readonly Lazy<SystemRecordPolicy> s_codex = new(() => Parse(CatalogResources.ReadBuiltIn(CodexResource)));

    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _folders = new(StringComparer.OrdinalIgnoreCase);

    public SystemRecordPolicy(IEnumerable<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var raw in entries)
        {
            var entry = raw?.Trim().Replace('/', '\\');
            if (string.IsNullOrEmpty(entry))
            {
                continue;
            }

            var folder = entry.EndsWith('\\');
            var relative = entry.Trim('\\');
            // Only plain relative paths: nothing can point above or outside the config folder.
            if (relative.Length == 0 || relative.Contains(':', StringComparison.Ordinal)
                || relative.Split('\\').Any(part => part is "" or "." or ".."))
            {
                continue;
            }

            (folder ? _folders : _files).Add(relative);
        }
    }

    public static SystemRecordPolicy None { get; } = new([]);

    /// <summary>Every file under the root is a system record (a root inside a system folder).</summary>
    private bool _all;

    /// <summary>
    /// This catalog (relative to <paramref name="configFolder"/>) seen from a scope <paramref name="root"/>: the same
    /// root keeps it, a parent root gets the entries under the config folder's relative path, a root inside the config
    /// folder keeps the entries below it (all of it when the root itself is a system folder), any other root none.
    /// </summary>
    public SystemRecordPolicy RelativeTo(string configFolder, string root)
    {
        ArgumentNullException.ThrowIfNull(configFolder);
        ArgumentNullException.ThrowIfNull(root);
        if (IsEmpty || Full(configFolder) is not { } config || Full(root) is not { } scope)
        {
            return None;
        }

        if (string.Equals(config, scope, StringComparison.OrdinalIgnoreCase))
        {
            return this;
        }

        if (config.StartsWith(scope + "\\", StringComparison.OrdinalIgnoreCase))
        {
            var prefix = config[(scope.Length + 1)..] + "\\";
            return Rebuilt(_files.Select(f => prefix + f), _folders.Select(f => prefix + f), all: false);
        }

        if (scope.StartsWith(config + "\\", StringComparison.OrdinalIgnoreCase))
        {
            var below = scope[(config.Length + 1)..];
            if (IsSystemFolder(below))
            {
                return Rebuilt([], [], all: true);
            }

            var cut = below + "\\";
            return Rebuilt(
                _files.Where(f => f.StartsWith(cut, StringComparison.OrdinalIgnoreCase)).Select(f => f[cut.Length..]),
                _folders.Where(f => f.StartsWith(cut, StringComparison.OrdinalIgnoreCase)).Select(f => f[cut.Length..]),
                all: false);
        }

        return None;
    }

    /// <summary>The union of several policies (a root may contain both the Claude and the Codex folder).</summary>
    public static SystemRecordPolicy Combine(params SystemRecordPolicy[] policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        var parts = policies.Where(p => !p.IsEmpty).ToList();
        return parts.Count switch
        {
            0 => None,
            1 => parts[0],
            _ => Rebuilt(parts.SelectMany(p => p._files), parts.SelectMany(p => p._folders), parts.Any(p => p._all)),
        };
    }

    private static SystemRecordPolicy Rebuilt(IEnumerable<string> files, IEnumerable<string> folders, bool all)
    {
        var policy = new SystemRecordPolicy([]) { _all = all };
        policy._files.UnionWith(files);
        policy._folders.UnionWith(folders);
        return policy;
    }

    private static string? Full(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>The built-in Claude Code catalog.</summary>
    public static SystemRecordPolicy Claude => s_claude.Value;

    /// <summary>The built-in Codex CLI catalog.</summary>
    public static SystemRecordPolicy Codex => s_codex.Value;

    public bool IsEmpty => !_all && _files.Count == 0 && _folders.Count == 0;

    /// <param name="relativeFolder">Folder path relative to the config folder, e.g. "plugins\synced".</param>
    public bool IsSystemFolder(string relativeFolder)
    {
        ArgumentNullException.ThrowIfNull(relativeFolder);
        if (_all)
        {
            return true;
        }

        for (var folder = Normalize(relativeFolder); folder.Length > 0; folder = Parent(folder))
        {
            if (_folders.Contains(folder))
            {
                return true;
            }
        }

        return false;
    }

    /// <param name="relativeFile">File path relative to the config folder, e.g. ".claude.json".</param>
    public bool IsSystemFile(string relativeFile)
    {
        ArgumentNullException.ThrowIfNull(relativeFile);
        var normalized = Normalize(relativeFile);
        return normalized.Length > 0 && (_all || _files.Contains(normalized) || IsSystemFolder(Parent(normalized)));
    }

    /// <summary>Reads a catalog <c>{"schemaVersion": 1, "paths": ["/.claude.json", "/backups/", …]}</c>.</summary>
    /// <exception cref="InvalidDataException">The catalog is not in this format.</exception>
    public static SystemRecordPolicy Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1
                || !root.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("system-records: schemaVersion 1 and a paths array are required.");
            }

            var entries = new List<string>();
            foreach (var item in paths.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException("system-records: every path must be a string.");
                }

                entries.Add(item.GetString()!);
            }

            return new SystemRecordPolicy(entries);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("system-records: invalid JSON.", ex);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("system-records: invalid schemaVersion.", ex);
        }
    }

    private static string Normalize(string path) => path.Replace('/', '\\').Trim('\\');

    private static string Parent(string path)
    {
        var separator = path.LastIndexOf('\\');
        return separator < 0 ? string.Empty : path[..separator];
    }
}
