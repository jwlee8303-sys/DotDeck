using DotDeck.Core.Editors;
using DotDeck.Core.Files;

namespace DotDeck.Core.Roles;

/// <summary>
/// Decides what a file is to Claude Code (and later other tools) from where it lives, not from its name alone:
/// <list type="number">
/// <item>Inside a configuration folder (the nearest <c>.claude</c> above the file): rules relative to that folder.
/// The folder is global when it sits directly in the home folder, project otherwise.</item>
/// <item>Outside: rules for the home folder, project roots ("anchor" folders) and any folder.</item>
/// </list>
/// Only paths are compared; the file system is asked only whether a folder holds <c>.claude</c> or <c>.git</c>.
/// </summary>
public sealed class FileRoleResolver
{
    private readonly IReadOnlyList<FileRoleCatalog> _catalogs;
    private readonly IFileStore _store;
    private readonly string _home;
    private readonly Func<IReadOnlyList<string>> _projectRoots;
    private readonly bool _includeManaged;

    /// <param name="home">The resolved home folder (<c>--home</c> / <c>DOTDECK_HOME</c> / user profile).</param>
    /// <param name="projectRoots">Root folders of the registered projects (asked on every call).</param>
    /// <param name="includeManaged">False in sandbox mode: organization folders outside the home are not recognized.</param>
    public FileRoleResolver(IReadOnlyList<FileRoleCatalog> catalogs, IFileStore store, string home,
        Func<IReadOnlyList<string>>? projectRoots = null, bool includeManaged = true)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(home);
        _catalogs = catalogs;
        _store = store;
        _home = Normalize(home);
        _projectRoots = projectRoots ?? (() => []);
        _includeManaged = includeManaged;
    }

    public string Home => _home;

    /// <summary>The role of <paramref name="path"/>, or null when no catalog knows it.</summary>
    public FileRole? Resolve(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string full;
        try
        {
            full = Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        foreach (var catalog in _catalogs)
        {
            if (Resolve(catalog, full) is { } role)
            {
                return role;
            }
        }

        return null;
    }

    /// <summary>The editor for a file without an override: the role's editor, else Markdown for .md files, else text.</summary>
    public EditorKind DefaultEditor(string path, FileRole? role) =>
        role?.Editor ?? (string.Equals(Path.GetFileName(path), "config.toml", StringComparison.OrdinalIgnoreCase)
            ? EditorKind.CodexConfig : IsMarkdown(path) ? EditorKind.Markdown : EditorKind.Text);

    public static bool IsMarkdown(string path) =>
        Path.GetExtension(path) is var ext
        && (ext.Equals(".md", StringComparison.OrdinalIgnoreCase) || ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase));

    private FileRole? Resolve(FileRoleCatalog catalog, string full)
    {
        var directory = Path.GetDirectoryName(full);
        if (directory is null)
        {
            return null;
        }

        if (_includeManaged && catalog.ManagedDir is { Length: > 0 } managed
            && RelativeTo(Normalize(managed), full) is { } managedRelative)
        {
            return Match(catalog, RuleBase.ManagedDir, managedRelative, ConfigScope.Managed, null, Normalize(managed), null);
        }

        // 1. Inside a configuration folder: only its own rules apply.
        if (NearestConfigDir(catalog, directory) is { } configDir)
        {
            var owner = Path.GetDirectoryName(configDir)!;
            var isGlobal = PathEquals(owner, _home);
            var relative = RelativeTo(configDir, full)!;
            var nestedNote = !isGlobal && IsNested(owner) ? catalog.NestedConfigDirNote : null;
            return Match(catalog, RuleBase.ConfigDir, relative, isGlobal ? ConfigScope.Global : ConfigScope.Project, configDir, owner, nestedNote);
        }

        // 2. Outside: home, project roots, any folder.
        var name = Path.GetFileName(full);
        if (PathEquals(directory, _home) && Match(catalog, RuleBase.Home, name, ConfigScope.Global, null, _home, null) is { } homeRole)
        {
            return homeRole;
        }

        var isAnchor = IsAnchor(catalog, directory);
        foreach (var rule in catalog.Rules)
        {
            if ((rule.Base == RuleBase.AnchorDir && isAnchor) || rule.Base == RuleBase.AnyDir)
            {
                if (rule.Compiled!.IsMatch(name))
                {
                    // A subfolder rule never claims a project root (the anchor rule for the same name does).
                    if (rule.Scope == RuleScope.Subdirectory && isAnchor)
                    {
                        continue;
                    }

                    return Create(catalog, rule, ScopeOf(rule.Scope, ConfigScope.Project), null, directory, null);
                }
            }
        }

        return null;
    }

    private static FileRole? Match(FileRoleCatalog catalog, RuleBase ruleBase, string relative, ConfigScope level,
        string? configDir, string baseRoot, string? extraNote)
    {
        foreach (var rule in catalog.Rules)
        {
            if (rule.Base != ruleBase || !rule.Compiled!.IsMatch(relative))
            {
                continue;
            }

            if ((rule.OnlyIn == "global" && level != ConfigScope.Global) || (rule.OnlyIn == "project" && level != ConfigScope.Project))
            {
                continue;
            }

            var role = Create(catalog, rule, ScopeOf(rule.Scope, level), configDir, baseRoot, level);
            return extraNote is null ? role : role with { Notes = [.. role.Notes, extraNote] };
        }

        return null;
    }

    private static FileRole Create(FileRoleCatalog catalog, FileRoleRule rule, ConfigScope scope, string? configDir, string baseRoot, ConfigScope? level)
    {
        var notes = new List<string>(rule.Notes ?? []);
        if (level == ConfigScope.Global && rule.GlobalNotes is { } g)
        {
            notes.AddRange(g);
        }
        else if (level == ConfigScope.Project && rule.ProjectNotes is { } p)
        {
            notes.AddRange(p);
        }

        return new FileRole(catalog.Product, rule.Role, catalog.RoleLabel(rule.Role), scope, configDir, baseRoot,
            FileRoleCatalog.EditorOf(rule.Editor), rule.Id, notes);
    }

    private static ConfigScope ScopeOf(RuleScope scope, ConfigScope level) => scope switch
    {
        RuleScope.Global => ConfigScope.Global,
        RuleScope.Project => ConfigScope.Project,
        RuleScope.Local => ConfigScope.Local,
        RuleScope.Subdirectory => ConfigScope.Subdirectory,
        RuleScope.Managed => ConfigScope.Managed,
        _ => level,
    };

    /// <summary>The deepest folder named like the catalog's configuration folder that contains <paramref name="directory"/>.</summary>
    private static string? NearestConfigDir(FileRoleCatalog catalog, string directory)
    {
        for (var current = directory; current is not null; current = Path.GetDirectoryName(current))
        {
            if (string.Equals(Path.GetFileName(current), catalog.ConfigDirName, StringComparison.OrdinalIgnoreCase)
                && Path.GetDirectoryName(current) is not null)
            {
                return current;
            }
        }

        return null;
    }

    /// <summary>A project root: holds the configuration folder or <c>.git</c>, or is a registered project root.</summary>
    private bool IsAnchor(FileRoleCatalog catalog, string directory)
    {
        if (_projectRoots().Any(root => PathEquals(SafeNormalize(root), directory)))
        {
            return true;
        }

        var git = Path.Combine(directory, ".git");
        return _store.DirectoryExists(Path.Combine(directory, catalog.ConfigDirName))
            || _store.DirectoryExists(git) || _store.FileExists(git);
    }

    /// <summary>A project folder below another project root (a monorepo package).</summary>
    private bool IsNested(string owner) =>
        _projectRoots().Select(SafeNormalize).Any(root => root.Length > 0 && !PathEquals(root, owner) && RelativeTo(root, owner) is not null);

    private static string SafeNormalize(string path)
    {
        try
        {
            return Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
    }

    /// <summary><paramref name="path"/> relative to <paramref name="root"/> with <c>/</c> separators, or null when outside.</summary>
    private static string? RelativeTo(string root, string path)
    {
        if (path.Length <= root.Length + 1 || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            || (path[root.Length] != Path.DirectorySeparatorChar && !root.EndsWith(Path.DirectorySeparatorChar)))
        {
            return null;
        }

        var start = root.EndsWith(Path.DirectorySeparatorChar) ? root.Length : root.Length + 1;
        return path[start..].Replace(Path.DirectorySeparatorChar, '/');
    }

    private static bool PathEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    internal static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        return full.Length > (root?.Length ?? 0) ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }
}
