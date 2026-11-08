using System.Text.Json.Nodes;

namespace DotDeck.Core.Explorer.Visibility;

/// <summary>A check the user set on one item (SPEC 2.3): show it (and, for folders, what is below) or hide it.</summary>
public enum VisibilityRule
{
    Include,
    Exclude,
}

/// <summary>Three-state check box of the edit mode.</summary>
public enum CheckState
{
    Checked,
    Unchecked,
    Partial,
}

/// <summary>
/// What a folder project shows. <see cref="Rules"/> hold only the checks the user changed (keys:
/// <see cref="RelativeKey"/>). <see cref="DefaultExtensions"/> null means "the app's global default".
/// </summary>
public sealed record VisibilitySettings
{
    private static readonly IReadOnlyDictionary<string, VisibilityRule> s_noRules =
        new Dictionary<string, VisibilityRule>(RelativeKey.Comparer);

    public static VisibilitySettings Default { get; } = new();

    public IReadOnlyDictionary<string, VisibilityRule> Rules { get; init; } = s_noRules;

    public IReadOnlyList<string>? DefaultExtensions { get; init; }

    /// <summary>Skip the extension check (decision step ②).</summary>
    public bool ShowAllFiles { get; init; }

    /// <summary>Unknown keys of the stored "visibility" object, written back unchanged.</summary>
    public JsonObject? Extra { get; init; }

    /// <summary>The extensions in effect: the project's own list, else the global default, else the preset.</summary>
    public IReadOnlyList<string> EffectiveExtensions(IReadOnlyList<string>? globalDefault) =>
        DefaultExtensions ?? globalDefault ?? ExtensionRules.ConfigDocsPreset;

    /// <summary>Rules from any key spelling (normalized; empty keys mean the root).</summary>
    public static IReadOnlyDictionary<string, VisibilityRule> CreateRules(IEnumerable<KeyValuePair<string, VisibilityRule>> rules)
    {
        var map = new Dictionary<string, VisibilityRule>(RelativeKey.Comparer);
        foreach (var (key, value) in rules)
        {
            map[RelativeKey.Normalize(key)] = value;
        }

        return map;
    }

    /// <summary>Value equality (records would compare the collections by reference).</summary>
    public bool Equals(VisibilitySettings? other) => ContentEquals(other);

    public override int GetHashCode() => HashCode.Combine(ShowAllFiles, Rules.Count, DefaultExtensions?.Count ?? -1);

    public bool ContentEquals(VisibilitySettings? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return ShowAllFiles == other.ShowAllFiles
            && SameList(DefaultExtensions, other.DefaultExtensions)
            && Rules.Count == other.Rules.Count
            && Rules.All(r => other.Rules.TryGetValue(r.Key, out var v) && v == r.Value)
            && JsonNode.DeepEquals(Extra, other.Extra);
    }

    internal static bool SameList(IReadOnlyList<string>? a, IReadOnlyList<string>? b) =>
        a is null ? b is null : b is not null && a.SequenceEqual(b, StringComparer.Ordinal);
}

/// <summary>Normal-mode layout of a project's explorer (SPEC 2.3).</summary>
public enum ExplorerViewMode
{
    Tree,
    Compact,
    Flat,
}

public enum FlatSort
{
    Name,

    /// <summary>The user's drag order (<see cref="ProjectViewSettings.FlatOrder"/>), unknown files after it by name.</summary>
    Custom,
}

/// <summary>How a project's explorer is laid out in normal mode, and the items pinned to the top.</summary>
public sealed record ProjectViewSettings
{
    public static ProjectViewSettings Default { get; } = new();

    public ExplorerViewMode Mode { get; init; } = ExplorerViewMode.Compact;

    public FlatSort FlatSort { get; init; }

    /// <summary>Keys of files in the user's flat order.</summary>
    public IReadOnlyList<string> FlatOrder { get; init; } = [];

    /// <summary>Keys of items shown at the top of the section, in order.</summary>
    public IReadOnlyList<string> Pins { get; init; } = [];

    public JsonObject? Extra { get; init; }

    public bool IsPinned(string key) => Pins.Contains(key, RelativeKey.Comparer);

    /// <summary>Value equality (records would compare the collections by reference).</summary>
    public bool Equals(ProjectViewSettings? other) => ContentEquals(other);

    public override int GetHashCode() => HashCode.Combine(Mode, FlatSort, Pins.Count, FlatOrder.Count);

    public bool ContentEquals(ProjectViewSettings? other) =>
        other is not null
        && Mode == other.Mode
        && FlatSort == other.FlatSort
        && FlatOrder.SequenceEqual(other.FlatOrder, RelativeKey.Comparer)
        && Pins.SequenceEqual(other.Pins, RelativeKey.Comparer)
        && JsonNode.DeepEquals(Extra, other.Extra);
}
