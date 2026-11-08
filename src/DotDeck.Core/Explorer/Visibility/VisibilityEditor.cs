namespace DotDeck.Core.Explorer.Visibility;

/// <param name="ClearedDirectFiles">Files checked one by one (non-default extension) whose Include rule goes away.</param>
/// <param name="ClearedRules">All rules below the folder that are removed.</param>
public sealed record ToggleImpact(int ClearedDirectFiles, int ClearedRules);

/// <summary>
/// Applies edit-mode check box changes and keeps the rules minimal: a rule is stored only where the wanted value
/// differs from what the item would inherit. Not thread-safe.
/// </summary>
public sealed class VisibilityEditor
{
    private readonly Dictionary<string, VisibilityRule> _rules;
    private readonly IReadOnlyList<string> _extensions;
    private VisibilitySettings _base;
    private VisibilityEvaluator? _evaluator;

    public VisibilityEditor(VisibilitySettings initial, IReadOnlyList<string> effectiveExtensions)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(effectiveExtensions);
        _base = initial;
        _extensions = effectiveExtensions;
        _rules = new Dictionary<string, VisibilityRule>(initial.Rules, RelativeKey.Comparer);
    }

    public VisibilitySettings Current => _base with { Rules = new Dictionary<string, VisibilityRule>(_rules, RelativeKey.Comparer) };

    public VisibilityEvaluator Evaluator => _evaluator ??= new VisibilityEvaluator(Current, _extensions);

    /// <summary>What checking or unchecking a folder removes (for the "직접 체크한 파일 N개도 해제됩니다" question).</summary>
    public ToggleImpact PreviewFolder(string folderKey, Func<string, bool> isDirectory)
    {
        ArgumentNullException.ThrowIfNull(isDirectory);
        var key = RelativeKey.Normalize(folderKey);
        var below = _rules.Where(r => RelativeKey.IsInside(r.Key, key)).ToList();
        var direct = below.Count(r => r.Value == VisibilityRule.Include
            && !Evaluator.PassesExtension(RelativeKey.NameOf(r.Key))
            && !isDirectory(r.Key));
        return new ToggleImpact(direct, below.Count);
    }

    /// <summary>Checks or unchecks a folder: every rule below it is removed, then its own rule is set (if needed).</summary>
    public void SetFolder(string folderKey, bool check)
    {
        var key = RelativeKey.Normalize(folderKey);
        foreach (var below in _rules.Keys.Where(k => RelativeKey.IsInside(k, key)).ToList())
        {
            _rules.Remove(below);
        }

        _rules.Remove(key);
        Changed();
        var wanted = check ? VisibilityRule.Include : VisibilityRule.Exclude;
        if (Evaluator.InheritedFolderState(key) != wanted)
        {
            _rules[key] = wanted;
            Changed();
        }
    }

    /// <summary>Checks or unchecks one file: a rule only when the wanted value differs from the inherited one.</summary>
    public void SetFile(string fileKey, bool check)
    {
        var key = RelativeKey.Normalize(fileKey);
        _rules.Remove(key);
        Changed();
        if (Evaluator.InheritedFileValue(key) != check)
        {
            _rules[key] = check ? VisibilityRule.Include : VisibilityRule.Exclude;
            Changed();
        }
    }

    /// <summary>Forces an Include rule on an item (new items that must be visible; empty folders need their own rule).</summary>
    public void ForceInclude(string key)
    {
        _rules[RelativeKey.Normalize(key)] = VisibilityRule.Include;
        Changed();
    }

    /// <summary>Checks (shows) or unchecks (hides) the "사용자 지정 파일" node.</summary>
    public void SetCustomFilesVisible(bool visible)
    {
        _base = _base with { HideCustomFiles = !visible };
        Changed();
    }

    /// <summary>Drops every rule (the "모두 기본값으로 초기화" choice).</summary>
    public void Clear()
    {
        _rules.Clear();
        Changed();
    }

    private void Changed() => _evaluator = null;
}
