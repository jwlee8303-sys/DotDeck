namespace DotDeck.Core.AiCli.Scope;

public enum PathExistence
{
    Exists,

    /// <summary>Definitely absent (file or path not found).</summary>
    Missing,

    /// <summary>Could not be determined (access denied, unsupported volume, I/O error).</summary>
    Unknown,
}

/// <summary>
/// Resolves an existing path to its final form: symbolic links and junctions followed, 8.3 short names expanded and
/// casing taken from disk. The Windows implementation uses <c>GetFinalPathNameByHandle</c>; tests use a fake.
/// </summary>
public interface IFinalPathResolver
{
    /// <summary>
    /// Whether an entry exists at <paramref name="fullPath"/> (links are not followed). Anything other than a definite
    /// "not found" must be <see cref="PathExistence.Unknown"/> so that the checker never skips an inaccessible link.
    /// </summary>
    PathExistence Probe(string fullPath);

    /// <summary>
    /// The final path of an existing entry in plain DOS form (<c>C:\…</c> or <c>\\server\share\…</c>), or null when it
    /// cannot be resolved (broken link, access denied, unsupported volume).
    /// </summary>
    string? ResolveFinalPath(string fullPath);
}

/// <summary>Resolver that performs no link resolution; for callers that only need lexical normalization.</summary>
public sealed class LexicalPathResolver : IFinalPathResolver
{
    public static LexicalPathResolver Instance { get; } = new();

    public PathExistence Probe(string fullPath) =>
        File.Exists(fullPath) || Directory.Exists(fullPath) ? PathExistence.Exists : PathExistence.Missing;

    public string? ResolveFinalPath(string fullPath) => fullPath;
}
