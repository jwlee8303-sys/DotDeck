using DotDeck.Core.Files;

namespace DotDeck.Core.Explorer;

/// <summary>
/// Decides whether a path is a secret file after following links: file symbolic links, folder links and junctions
/// anywhere in the path, hop by hop to the final target. Any secret name along the chain counts.
/// Hard links cannot be told apart from ordinary files and are a known limitation.
/// </summary>
public static class SecretFileResolver
{
    /// <summary>Hops followed before the chain is treated as a cycle (and therefore as secret).</summary>
    public const int MaxHops = 40;

    public static bool IsSecretFile(IFileStore store, string path)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (SecretFilePolicy.IsSecret(Path.GetFileName(path)))
        {
            return true;
        }

        try
        {
            var current = Path.GetFullPath(path);
            for (var hop = 0; hop < MaxHops; hop++)
            {
                if (ResolveOnce(store, current) is not { } next)
                {
                    return false;
                }

                if (SecretFilePolicy.IsSecret(Path.GetFileName(next)))
                {
                    return true;
                }

                current = next;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // The chain cannot be inspected: be conservative (display policy only, never blocks opening).
            return true;
        }
    }

    /// <summary>Replaces the first linked prefix of <paramref name="path"/> with its target; null when no part is a link.</summary>
    private static string? ResolveOnce(IFileStore store, string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        var parts = path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var prefix = root;
        for (var i = 0; i < parts.Length; i++)
        {
            prefix = Path.Combine(prefix, parts[i]);
            if (store.GetLinkTarget(prefix) is { } target)
            {
                return Path.GetFullPath(Path.Combine([target, .. parts[(i + 1)..]]));
            }
        }

        return null;
    }
}
