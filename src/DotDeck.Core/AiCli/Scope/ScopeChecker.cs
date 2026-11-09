namespace DotDeck.Core.AiCli.Scope;

public enum ScopeAccess
{
    Read,
    Write,
}

public enum ScopeOutcome
{
    /// <summary>The final path is inside an allowed root for the requested access.</summary>
    Inside,

    /// <summary>The final path is outside every allowed root (or only inside a read-only root for a write).</summary>
    Outside,

    /// <summary>The path could not be normalized or its final location could not be determined.</summary>
    Unresolved,
}

public sealed record ScopeDecision(ScopeOutcome Outcome, string? FinalPath, string Reason)
{
    public bool IsInside => Outcome == ScopeOutcome.Inside;
}

/// <summary>An allowed root. Read-only roots allow reads but not writes.</summary>
public sealed record ScopeRoot(string Path, bool ReadOnly = false);

/// <summary>
/// Decides whether a path used by an AI CLI tool stays inside the session scope (SPEC 11.7). The path is normalized
/// lexically, then the deepest existing ancestor is resolved through <see cref="IFinalPathResolver"/> (links,
/// junctions, 8.3 names, casing) and the not-yet-existing remainder is appended. Roots are resolved the same way, so
/// a link inside the scope that points outside is outside, and a root reached through a link is still inside.
/// </summary>
public sealed class ScopeChecker
{
    private readonly IFinalPathResolver _resolver;
    private readonly IReadOnlyList<(string Final, bool ReadOnly)> _roots;

    public ScopeChecker(IEnumerable<ScopeRoot> roots, IFinalPathResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(roots);
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

        var list = new List<(string, bool)>();
        foreach (var root in roots)
        {
            var normalized = PathNormalizer.Normalize(root.Path, cwd: null);
            if (normalized.Kind != NormalizedPathKind.Path)
            {
                continue; // An unusable root grants nothing.
            }

            // A root whose final location cannot be determined grants nothing: registering its lexical form could
            // accept paths under an unverified link.
            var final = Resolve(normalized.FullPath!);
            if (final is not null)
            {
                list.Add((final, root.ReadOnly));
            }
        }

        _roots = list;
    }

    /// <summary>Final forms of the allowed roots (for diagnostics and tests).</summary>
    public IEnumerable<string> FinalRoots => _roots.Select(r => r.Final);

    public ScopeDecision Check(string? rawPath, string? cwd, ScopeAccess access, string? home = null)
    {
        var normalized = PathNormalizer.Normalize(rawPath, cwd, home);
        switch (normalized.Kind)
        {
            case NormalizedPathKind.NullDevice:
                return new(ScopeOutcome.Inside, null, "NUL 장치");
            case NormalizedPathKind.Invalid:
                return new(ScopeOutcome.Unresolved, null, normalized.Error ?? "확인할 수 없는 경로");
        }

        var final = Resolve(normalized.FullPath!);
        if (final is null)
        {
            return new(ScopeOutcome.Unresolved, normalized.FullPath, $"실제 위치를 확인할 수 없음: {normalized.FullPath}");
        }

        var matched = false;
        foreach (var (root, readOnly) in _roots)
        {
            if (!PathNormalizer.IsSameOrUnder(final, root))
            {
                continue;
            }

            matched = true;
            if (access == ScopeAccess.Read || !readOnly)
            {
                return new(ScopeOutcome.Inside, final, "세션 범위 안");
            }
        }

        return matched
            ? new(ScopeOutcome.Outside, final, $"읽기 전용 범위: {final}")
            : new(ScopeOutcome.Outside, final, $"DotDeck 세션 범위 밖: {final}");
    }

    /// <summary>
    /// Final form of <paramref name="fullPath"/>: the deepest existing ancestor resolved by the resolver plus the
    /// remaining segments. Null when no ancestor exists or the existing part cannot be resolved.
    /// </summary>
    private string? Resolve(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            return null;
        }

        var existing = fullPath;
        var remainder = new Stack<string>();
        while (true)
        {
            var existence = _resolver.Probe(existing);
            if (existence == PathExistence.Exists)
            {
                break;
            }

            if (existence == PathExistence.Unknown)
            {
                return null; // e.g. an inaccessible junction: never walk past it.
            }

            var parent = Path.GetDirectoryName(existing);
            if (parent is null)
            {
                return null; // Not even the volume exists.
            }

            var name = Path.GetFileName(existing);
            remainder.Push(name);
            existing = parent;
        }

        string? final;
        try
        {
            final = _resolver.ResolveFinalPath(existing);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            final = null;
        }

        if (final is null)
        {
            return null;
        }

        final = PathNormalizer.TrimEnd(final);
        while (remainder.Count > 0)
        {
            final = Path.Join(final, remainder.Pop());
        }

        return PathNormalizer.TrimEnd(final);
    }
}
