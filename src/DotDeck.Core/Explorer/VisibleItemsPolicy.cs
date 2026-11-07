namespace DotDeck.Core.Explorer;

/// <summary>
/// The user's "show only these" list of a project (relative paths). Empty means everything is shown.
/// A checked folder shows its whole content; a folder is also shown when something inside it is checked.
/// </summary>
public sealed class VisibleItemsPolicy
{
    private readonly HashSet<string> _items = new(StringComparer.OrdinalIgnoreCase);

    public VisibleItemsPolicy(IEnumerable<string>? items)
    {
        foreach (var item in items ?? [])
        {
            var normalized = Normalize(item);
            if (normalized.Length > 0)
            {
                _items.Add(normalized);
            }
        }
    }

    public bool ShowsAll => _items.Count == 0;

    public bool IsVisible(string relativePath, bool isDirectory)
    {
        if (ShowsAll)
        {
            return true;
        }

        var path = Normalize(relativePath);
        if (IsChecked(path) || HasCheckedAncestor(path))
        {
            return true;
        }

        var prefix = path + Path.DirectorySeparatorChar;
        return isDirectory && _items.Any(i => i.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsChecked(string relativePath) => _items.Contains(Normalize(relativePath));

    public bool HasCheckedAncestor(string relativePath)
    {
        var path = Normalize(relativePath);
        for (var i = path.LastIndexOf(Path.DirectorySeparatorChar); i > 0; i = path.LastIndexOf(Path.DirectorySeparatorChar, i - 1))
        {
            if (_items.Contains(path[..i]))
            {
                return true;
            }
        }

        return false;
    }

    public static string Normalize(string path) =>
        path.Replace('/', Path.DirectorySeparatorChar).Trim().Trim(Path.DirectorySeparatorChar);
}
