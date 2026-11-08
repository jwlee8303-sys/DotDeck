namespace DotDeck.Core.SystemConfig;

public sealed record PathEntryIssue(int Index, string Message);

/// <summary>PATH value helpers: split, join, and warnings for missing folders and duplicates (SPEC §8).</summary>
public static class PathList
{
    public static IReadOnlyList<string> Split(string? value) =>
        (value ?? string.Empty).Split(';').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();

    public static string Join(IEnumerable<string> entries) =>
        string.Join(';', entries.Select(e => e.Trim()).Where(e => e.Length > 0));

    /// <param name="expand">Expands %VARS% (e.g. <see cref="Environment.ExpandEnvironmentVariables"/>).</param>
    public static IReadOnlyList<PathEntryIssue> Analyze(IReadOnlyList<string> entries, Func<string, bool> directoryExists, Func<string, string> expand)
    {
        var issues = new List<PathEntryIssue>();
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < entries.Count; i++)
        {
            var expanded = expand(entries[i].Trim().Trim('"'));
            var key = expanded.TrimEnd('\\', '/');
            if (seen.TryGetValue(key, out var first))
            {
                issues.Add(new PathEntryIssue(i, $"{first + 1}번 항목과 중복됩니다."));
            }
            else
            {
                seen[key] = i;
            }

            if (expanded.Contains('%', StringComparison.Ordinal))
            {
                continue; // unresolved variable: cannot check
            }

            if (!directoryExists(expanded))
            {
                issues.Add(new PathEntryIssue(i, "폴더가 없습니다."));
            }
        }

        return issues;
    }
}
