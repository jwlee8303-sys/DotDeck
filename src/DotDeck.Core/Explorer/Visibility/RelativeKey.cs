namespace DotDeck.Core.Explorer.Visibility;

/// <summary>
/// Project-relative path keys as used in memory: <c>\</c> separators, no leading or trailing separator, <c>""</c>
/// for the project root. Compared without case, like <see cref="ExplorerTreeBuilder.KeyComparer"/>. Stored in
/// projects.json with <c>/</c> separators.
/// </summary>
public static class RelativeKey
{
    public const char Separator = '\\';

    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Trim().Replace('/', Separator).Trim(Separator);
    }

    public static string ToStorage(string key) => Normalize(key).Replace(Separator, '/');

    /// <summary>"a\b" → "a", "a" → "", "" → null.</summary>
    public static string? Parent(string key)
    {
        if (key.Length == 0)
        {
            return null;
        }

        var index = key.LastIndexOf(Separator);
        return index < 0 ? string.Empty : key[..index];
    }

    /// <summary>The last segment ("a\b.md" → "b.md").</summary>
    public static string NameOf(string key)
    {
        var index = key.LastIndexOf(Separator);
        return index < 0 ? key : key[(index + 1)..];
    }

    /// <summary>True when <paramref name="key"/> is <paramref name="folderKey"/> or below it ("" contains everything).</summary>
    public static bool IsSameOrInside(string key, string folderKey) =>
        folderKey.Length == 0
        || Comparer.Equals(key, folderKey)
        || (key.Length > folderKey.Length && key[folderKey.Length] == Separator
            && key.StartsWith(folderKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when <paramref name="key"/> is strictly below <paramref name="folderKey"/>.</summary>
    public static bool IsInside(string key, string folderKey) =>
        IsSameOrInside(key, folderKey) && !Comparer.Equals(key, folderKey);

    /// <summary>Key of an absolute path below <paramref name="root"/>, or null when it is outside.</summary>
    public static string? FromFullPath(string root, string fullPath)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(fullPath);
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative == ".")
        {
            return string.Empty;
        }

        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return null;
        }

        return Normalize(relative);
    }

    /// <summary>Joins a parent key and a child name.</summary>
    public static string Combine(string parent, string name) => parent.Length == 0 ? name : parent + Separator + name;
}
