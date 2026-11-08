using DotDeck.Core.FileOperations;

namespace DotDeck.Core.Explorer.Visibility;

/// <summary>
/// Keeps a project's rules, pins and flat order on the right paths when items are renamed, moved or deleted
/// (SPEC 2.3). Pure: callers pass the changes of a file operation and store the result.
/// </summary>
public static class ExplorerPathRewriter
{
    /// <returns>The same instances when nothing changed.</returns>
    public static (VisibilitySettings Visibility, ProjectViewSettings View) Apply(
        VisibilitySettings visibility, ProjectViewSettings view, string rootPath, IReadOnlyList<PathChange> changes)
    {
        ArgumentNullException.ThrowIfNull(visibility);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(rootPath);
        ArgumentNullException.ThrowIfNull(changes);
        var rules = visibility.Rules.ToList();
        var pins = view.Pins.ToList();
        var order = view.FlatOrder.ToList();
        var changed = false;
        foreach (var change in changes)
        {
            switch (change)
            {
                case PathChange.Moved(var from, var to):
                    var fromKey = RelativeKey.FromFullPath(rootPath, from);
                    if (fromKey is null or "")
                    {
                        break;
                    }

                    var toKey = RelativeKey.FromFullPath(rootPath, to);
                    changed |= toKey is null or ""
                        ? Remove(rules, pins, order, fromKey)
                        : Rename(rules, pins, order, fromKey, toKey);
                    break;
                case PathChange.Removed(var path):
                    if (RelativeKey.FromFullPath(rootPath, path) is { Length: > 0 } removed)
                    {
                        changed |= Remove(rules, pins, order, removed);
                    }

                    break;
            }
        }

        if (!changed)
        {
            return (visibility, view);
        }

        return (
            visibility with { Rules = VisibilitySettings.CreateRules(rules) },
            view with { Pins = pins.Distinct(RelativeKey.Comparer).ToList(), FlatOrder = order.Distinct(RelativeKey.Comparer).ToList() });
    }

    private static bool Remove(List<KeyValuePair<string, VisibilityRule>> rules, List<string> pins, List<string> order, string key) =>
        rules.RemoveAll(r => RelativeKey.IsSameOrInside(r.Key, key)) > 0
        | pins.RemoveAll(p => RelativeKey.IsSameOrInside(p, key)) > 0
        | order.RemoveAll(o => RelativeKey.IsSameOrInside(o, key)) > 0;

    private static bool Rename(List<KeyValuePair<string, VisibilityRule>> rules, List<string> pins, List<string> order, string from, string to)
    {
        var changed = false;
        for (var i = 0; i < rules.Count; i++)
        {
            if (Moved(rules[i].Key, from, to) is { } key)
            {
                rules[i] = new KeyValuePair<string, VisibilityRule>(key, rules[i].Value);
                changed = true;
            }
        }

        changed |= RenameAll(pins, from, to);
        changed |= RenameAll(order, from, to);
        return changed;
    }

    private static bool RenameAll(List<string> keys, string from, string to)
    {
        var changed = false;
        for (var i = 0; i < keys.Count; i++)
        {
            if (Moved(keys[i], from, to) is { } key)
            {
                keys[i] = key;
                changed = true;
            }
        }

        return changed;
    }

    private static string? Moved(string key, string from, string to) =>
        RelativeKey.IsSameOrInside(key, from) ? to + key[from.Length..] : null;
}

/// <summary>Makes new items (created, pasted, duplicated, moved in) visible right away (SPEC 2.3).</summary>
public static class VisibilityAutoInclude
{
    /// <param name="hasVisibleContent">For folders: whether something inside is already shown (null: unknown).</param>
    public static VisibilitySettings EnsureVisible(
        VisibilitySettings settings, IReadOnlyList<string> extensions, string key, bool isDirectory, bool? hasVisibleContent = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = RelativeKey.Normalize(key);
        if (normalized.Length == 0)
        {
            return settings;
        }

        var editor = new VisibilityEditor(settings, extensions);
        if (!isDirectory)
        {
            if (editor.Evaluator.IsFileVisible(normalized))
            {
                return settings;
            }

            editor.SetFile(normalized, check: true);
            return editor.Current;
        }

        // A folder is shown when something inside is shown or it has its own Include rule (an empty new folder).
        if (editor.Evaluator.FolderState(normalized) == VisibilityRule.Include
            && (hasVisibleContent == true || editor.Evaluator.RuleOf(normalized) == VisibilityRule.Include))
        {
            return settings;
        }

        editor.ForceInclude(normalized);
        return editor.Current;
    }
}
