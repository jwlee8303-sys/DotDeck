using System.Text;
using System.Text.RegularExpressions;
using DotDeck.Core.Explorer.Visibility;

namespace DotDeck.Core.Explorer;

/// <summary>
/// The explorer's file filter ("*.md", "agent", "docs\*.md; *.json"). Terms are separated by ';' or ',' and any term
/// may match. A term with '*' or '?' is a wildcard pattern over the whole name; without them it matches part of the
/// name. A term with a path separator ('\' or '/') is matched against the path relative to the project root instead
/// (a wildcard term then matches the end of the path, at a folder boundary). Case-insensitive.
/// </summary>
public sealed class FileNameFilter
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(200);

    private readonly IReadOnlyList<Term> _terms;

    private FileNameFilter(string text, IReadOnlyList<Term> terms)
    {
        Text = text;
        _terms = terms;
    }

    /// <summary>The query as typed (trimmed).</summary>
    public string Text { get; }

    /// <summary>The filter for <paramref name="text"/>, or null when it has no terms (no filtering).</summary>
    public static FileNameFilter? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var terms = text.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(CreateTerm)
            .ToList();
        return terms.Count == 0 ? null : new FileNameFilter(text.Trim(), terms);
    }

    /// <summary>True when the node's name (or its relative path, for terms with a separator) matches a term.</summary>
    public bool IsMatch(NodeInfo node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var path = node.RelativePath is { Length: > 0 } relative ? relative : node.Name;
        foreach (var term in _terms)
        {
            if (term.IsMatch(term.MatchesPath ? path : node.Name))
            {
                return true;
            }
        }

        return false;
    }

    private static Term CreateTerm(string text)
    {
        var normalized = text.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var matchesPath = normalized.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal);
        if (normalized.IndexOfAny(['*', '?']) < 0)
        {
            return new Term(matchesPath, normalized, null);
        }

        var pattern = new StringBuilder(matchesPath ? @"(?:^|\\)" : "^");
        foreach (var c in normalized.TrimStart(Path.DirectorySeparatorChar))
        {
            pattern.Append(c switch
            {
                '*' => matchesPath ? @"[^\\]*" : ".*",
                '?' => matchesPath ? @"[^\\]" : ".",
                _ => Regex.Escape(c.ToString()),
            });
        }

        pattern.Append('$');
        var regex = new Regex(pattern.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline, MatchTimeout);
        return new Term(matchesPath, normalized, regex);
    }

    private sealed record Term(bool MatchesPath, string Text, Regex? Pattern)
    {
        public bool IsMatch(string value)
        {
            if (Pattern is null)
            {
                return value.Contains(Text, StringComparison.OrdinalIgnoreCase);
            }

            try
            {
                return Pattern.IsMatch(value);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }
    }
}

/// <param name="Files">Matching files, each with <see cref="NodeInfo.FlatDirectory"/> set to the folder it is in.</param>
/// <param name="IsTruncated">The result or entry limit was reached: there may be more matches.</param>
/// <param name="RootError">Why the root could not be listed; <see cref="Files"/> is then empty.</param>
public sealed record FileSearchResult(
    ProjectRootState RootState,
    IReadOnlyList<NodeInfo> Files,
    bool IsTruncated,
    string? RootError);

/// <summary>
/// Finds the files of a project source whose names match a <see cref="FileNameFilter"/>. It walks whatever the source
/// shows: the visible layout in normal mode, every item in visibility edit mode. Links to folders are not followed.
/// Performs file system I/O: call it off the UI thread.
/// </summary>
public static class ExplorerFileSearch
{
    public const int DefaultMaxResults = 1_000;

    public const int DefaultEntryBudget = 200_000;

    public static FileSearchResult Find(
        IProjectSource source,
        FileNameFilter filter,
        int maxResults = DefaultMaxResults,
        int entryBudget = DefaultEntryBudget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(filter);
        var state = source.GetRootState();
        if (state == ProjectRootState.Missing)
        {
            return new FileSearchResult(state, [], false, null);
        }

        IReadOnlyList<NodeInfo> rootChildren;
        try
        {
            rootChildren = source.GetChildren(null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FileSearchResult(state, [], false, ex.Message);
        }

        var files = new List<NodeInfo>();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<(IReadOnlyList<NodeInfo> Children, string Directory)>();
        pending.Enqueue((rootChildren, string.Empty));
        var budget = entryBudget - rootChildren.Count;
        var truncated = false;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (children, directory) = pending.Dequeue();
            foreach (var child in children)
            {
                if (child.CanExpand)
                {
                    if (child.IsLink || !visitedFolders.Add(child.Id))
                    {
                        continue;
                    }

                    if (budget <= 0)
                    {
                        truncated = true;
                        continue;
                    }

                    IReadOnlyList<NodeInfo> grandChildren;
                    try
                    {
                        grandChildren = source.GetChildren(child);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }

                    budget -= grandChildren.Count;
                    pending.Enqueue((grandChildren, child.RelativePath ?? JoinDisplay(directory, child.Name)));
                    continue;
                }

                if (!filter.IsMatch(child) || !seenFiles.Add(child.IsCustomFile ? "custom:" + child.Id : child.Id))
                {
                    continue;
                }

                if (files.Count >= maxResults)
                {
                    truncated = true;
                    break;
                }

                var folder = child.RelativePath is { Length: > 0 } relative ? RelativeKey.Parent(relative) ?? string.Empty : directory;
                files.Add(child with { FlatDirectory = folder, IsPinned = false, Chain = null });
            }

            if (files.Count >= maxResults && truncated)
            {
                break;
            }
        }

        var sorted = files
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.FlatDirectory, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Id, StringComparer.Ordinal)
            .ToList();
        return new FileSearchResult(state, sorted, truncated, null);
    }

    private static string JoinDisplay(string directory, string name) =>
        directory.Length == 0 ? name : directory + Path.DirectorySeparatorChar + name;
}
