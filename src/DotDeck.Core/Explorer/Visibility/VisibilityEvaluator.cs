namespace DotDeck.Core.Explorer.Visibility;

/// <summary>
/// Decides what a project shows, using only the rule dictionary and parent walks (no file system access).
/// A file is shown by: ① its own rule → ② when <see cref="VisibilitySettings.ShowAllFiles"/> is off, hidden
/// unless its name ends with a default extension → ③ the nearest folder rule above it → ④ shown.
/// </summary>
public sealed class VisibilityEvaluator
{
    private readonly IReadOnlyDictionary<string, VisibilityRule> _rules;
    private readonly IReadOnlyList<string> _extensions;

    // Folder key → which rule values exist strictly below it.
    private readonly Dictionary<string, (bool Include, bool Exclude)> _below = new(RelativeKey.Comparer);

    public VisibilityEvaluator(VisibilitySettings settings, IReadOnlyList<string> effectiveExtensions)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(effectiveExtensions);
        Settings = settings;
        _rules = settings.Rules;
        _extensions = effectiveExtensions;
        foreach (var (key, value) in _rules)
        {
            for (var parent = RelativeKey.Parent(key); parent is not null; parent = RelativeKey.Parent(parent))
            {
                _below.TryGetValue(parent, out var flags);
                _below[parent] = value == VisibilityRule.Include ? flags with { Include = true } : flags with { Exclude = true };
            }
        }
    }

    public VisibilitySettings Settings { get; }

    public IReadOnlyList<string> Extensions => _extensions;

    public VisibilityRule? RuleOf(string key) => _rules.TryGetValue(key, out var rule) ? rule : null;

    public bool PassesExtension(string fileName) => Settings.ShowAllFiles || ExtensionRules.Matches(fileName, _extensions);

    public bool IsFileVisible(string fileKey) =>
        RuleOf(fileKey) is { } own ? own == VisibilityRule.Include : InheritedFileValue(fileKey);

    /// <summary>Steps ②–④: what the file shows without a rule of its own.</summary>
    public bool InheritedFileValue(string fileKey) =>
        PassesExtension(RelativeKey.NameOf(fileKey))
        && (RelativeKey.Parent(fileKey) is not { } parent || FolderState(parent) == VisibilityRule.Include);

    /// <summary>The folder's own rule, else the nearest rule above it, else Include.</summary>
    public VisibilityRule FolderState(string folderKey)
    {
        for (string? key = folderKey; key is not null; key = RelativeKey.Parent(key))
        {
            if (_rules.TryGetValue(key, out var rule))
            {
                return rule;
            }
        }

        return VisibilityRule.Include;
    }

    /// <summary>What a folder inherits from above (its own rule ignored).</summary>
    public VisibilityRule InheritedFolderState(string folderKey) =>
        RelativeKey.Parent(folderKey) is { } parent ? FolderState(parent) : VisibilityRule.Include;

    public bool HasRuleBelow(string folderKey, VisibilityRule value) =>
        _below.TryGetValue(folderKey, out var flags) && (value == VisibilityRule.Include ? flags.Include : flags.Exclude);

    /// <summary>
    /// Nothing below the folder can be shown: it is excluded and no Include rule sits below it. Lets the tree query
    /// skip listing such folders.
    /// </summary>
    public bool IsSubtreeDead(string folderKey) =>
        FolderState(folderKey) == VisibilityRule.Exclude && !HasRuleBelow(folderKey, VisibilityRule.Include);

    /// <summary>
    /// Edit-mode check box. Files: shown or not (so a non-default extension under a checked folder is unchecked).
    /// Folders: their state, Partial when a rule below differs. Files hidden only by their extension do not make
    /// a folder partial.
    /// </summary>
    public CheckState CheckStateOf(string key, bool isDirectory)
    {
        if (!isDirectory)
        {
            return IsFileVisible(key) ? CheckState.Checked : CheckState.Unchecked;
        }

        return FolderState(key) == VisibilityRule.Include
            ? HasRuleBelow(key, VisibilityRule.Exclude) ? CheckState.Partial : CheckState.Checked
            : HasRuleBelow(key, VisibilityRule.Include) ? CheckState.Partial : CheckState.Unchecked;
    }
}
