namespace DotDeck.Core.AiCli.Scope;

public enum NormalizedPathKind
{
    /// <summary>A regular local or UNC path.</summary>
    Path,

    /// <summary>The NUL device: writes go nowhere, so it is always harmless.</summary>
    NullDevice,

    /// <summary>Empty, malformed, a device namespace or otherwise not checkable.</summary>
    Invalid,
}

public sealed record NormalizedPath(NormalizedPathKind Kind, string? FullPath, string? Error)
{
    public static NormalizedPath Invalid(string error) => new(NormalizedPathKind.Invalid, null, error);
}

/// <summary>
/// Lexical normalization of a path given to an AI CLI tool (SPEC 11.7): quotes, <c>\\?\</c> and UNC prefixes,
/// alternate data streams, reserved device names, <c>~</c>, relative paths and <c>..</c>. It does not touch the disk;
/// <see cref="ScopeChecker"/> resolves links afterwards.
/// </summary>
public static class PathNormalizer
{
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <param name="raw">Path as written by the tool or command.</param>
    /// <param name="cwd">Absolute directory relative paths are resolved against; null rejects relative paths.</param>
    /// <param name="home">Home folder used for <c>~</c>; null rejects <c>~</c> paths.</param>
    public static NormalizedPath Normalize(string? raw, string? cwd, string? home = null)
    {
        var path = Unquote(raw);
        if (string.IsNullOrWhiteSpace(path))
        {
            return NormalizedPath.Invalid("빈 경로");
        }

        // Trailing whitespace is literal in an extended path; trimming it would point somewhere else.
        if ((path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\??\", StringComparison.Ordinal))
            && raw is { Length: > 0 } && char.IsWhiteSpace(raw[^1]))
        {
            return NormalizedPath.Invalid("확장 경로 끝의 공백은 확인할 수 없습니다");
        }

        if (path.Contains('\0', StringComparison.Ordinal))
        {
            return NormalizedPath.Invalid("잘못된 문자가 있는 경로");
        }

        var extended = path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\??\", StringComparison.Ordinal);
        if (extended && path.Contains('/', StringComparison.Ordinal))
        {
            return NormalizedPath.Invalid("확장 경로의 '/'는 이름의 일부라 확인할 수 없습니다");
        }

        path = path.Replace('/', '\\');

        // Namespace prefixes.
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            path = @"\\" + path[8..];
        }
        else if (extended)
        {
            var rest = path[4..];
            // Require "X:\": a bare "\\?\C:" is the volume itself, not a drive-relative path.
            if (rest.Length < 3 || !HasDriveRoot(rest))
            {
                return NormalizedPath.Invalid("장치 네임스페이스 경로는 확인할 수 없습니다");
            }

            path = rest;
        }

        // Extended paths skip Win32 normalization: "NUL.txt", "a." or ".." are literal names there. Converting them
        // to the normal form would change the target, so such paths are rejected instead of reinterpreted.
        if (extended && HasSpecialSegment(path))
        {
            return NormalizedPath.Invalid("확장 경로에 일반 경로와 뜻이 달라지는 이름이 있습니다");
        }
        else if (path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            var rest = path[4..];
            if (string.Equals(rest, "NUL", StringComparison.OrdinalIgnoreCase))
            {
                return new(NormalizedPathKind.NullDevice, null, null);
            }

            return NormalizedPath.Invalid("장치 경로는 확인할 수 없습니다");
        }

        // Home expansion (Claude expands ~ for file tools; shell commands may still contain it).
        if (path == "~" || path.StartsWith(@"~\", StringComparison.Ordinal))
        {
            if (home is null || !Path.IsPathFullyQualified(home))
            {
                return NormalizedPath.Invalid("~ 경로를 해석할 수 없습니다");
            }

            path = home + path[1..];
        }

        // Drive-relative paths ("C:foo") resolve against that drive's own current directory, which is unknown
        // unless it is the drive of cwd.
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path.Length == 2 || path[2] != '\\'))
        {
            if (cwd is null || !cwd.StartsWith(path[..2], StringComparison.OrdinalIgnoreCase))
            {
                return NormalizedPath.Invalid("드라이브 상대 경로는 확인할 수 없습니다");
            }

            path = path.Length == 2 ? cwd : Path.Combine(cwd, path[2..]);
        }

        if (!Path.IsPathFullyQualified(path))
        {
            if (cwd is null || !Path.IsPathFullyQualified(cwd))
            {
                return NormalizedPath.Invalid("상대 경로의 기준 폴더가 없습니다");
            }
        }

        path = StripStreams(path);
        if (path.Length == 0)
        {
            return NormalizedPath.Invalid("잘못된 경로");
        }

        string full;
        try
        {
            full = Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : Path.GetFullPath(path, cwd!);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return NormalizedPath.Invalid("잘못된 경로");
        }

        // Win32 normalization maps reserved device names to device paths such as "\\.\NUL".
        if (string.Equals(full, @"\\.\NUL", StringComparison.OrdinalIgnoreCase))
        {
            return new(NormalizedPathKind.NullDevice, null, null);
        }

        if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return NormalizedPath.Invalid("장치 경로는 확인할 수 없습니다");
        }

        // Reserved names in any segment ("C:\x\con.txt" opens the console, not a file).
        foreach (var segment in Segments(full))
        {
            var stem = segment;
            var dot = stem.IndexOf('.', StringComparison.Ordinal);
            if (dot >= 0)
            {
                stem = stem[..dot];
            }

            stem = stem.TrimEnd(' ');
            if (ReservedDeviceNames.Contains(stem))
            {
                return string.Equals(stem, "NUL", StringComparison.OrdinalIgnoreCase)
                    ? new(NormalizedPathKind.NullDevice, null, null)
                    : NormalizedPath.Invalid("예약된 장치 이름이 있는 경로");
            }
        }

        return new(NormalizedPathKind.Path, TrimEnd(full), null);
    }

    /// <summary>Removes surrounding whitespace and one pair of matching quotes.</summary>
    public static string Unquote(string? raw)
    {
        var s = (raw ?? string.Empty).Trim();
        if (s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[^1] == s[0])
        {
            s = s[1..^1]; // Quoted content is taken literally.
        }

        return s;
    }

    /// <summary>Path without a trailing separator, except for roots (<c>C:\</c>, <c>\\server\share\</c>).</summary>
    public static string TrimEnd(string fullPath) => Path.TrimEndingDirectorySeparator(fullPath);

    /// <summary>True when <paramref name="path"/> equals <paramref name="root"/> or lies under it (whole segments, case-insensitive).</summary>
    public static bool IsSameOrUnder(string path, string root)
    {
        var p = TrimEnd(path);
        var r = TrimEnd(root);
        if (string.Equals(p, r, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return p.Length > r.Length
            && p.StartsWith(r, StringComparison.OrdinalIgnoreCase)
            && (r.EndsWith('\\') || p[r.Length] == '\\');
    }

    private static bool HasSpecialSegment(string path)
    {
        var root = HasDriveRoot(path) ? 3 : (path.StartsWith(@"\\", StringComparison.Ordinal) ? 2 : 0);
        var body = path[Math.Min(root, path.Length)..];
        if (body.EndsWith('\\'))
        {
            body = body[..^1]; // A single trailing separator is harmless.
        }

        if (body.Length == 0)
        {
            return false;
        }

        foreach (var segment in body.Split('\\'))
        {
            var colon = segment.IndexOf(':', StringComparison.Ordinal);
            var name = colon >= 0 ? segment[..colon] : segment;
            if (name.Length == 0 || name == "." || name == ".." || name.EndsWith('.') || name.EndsWith(' ') || name.StartsWith(' '))
            {
                return true;
            }

            var dot = name.IndexOf('.', StringComparison.Ordinal);
            var stem = (dot >= 0 ? name[..dot] : name).TrimEnd(' ');
            if (ReservedDeviceNames.Contains(stem))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasDriveRoot(string path) =>
        path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path.Length == 2 || path[2] == '\\');

    /// <summary>
    /// Drops alternate data stream suffixes (<c>a.txt:s</c>, <c>a.txt::$DATA</c>, <c>dir::$INDEX_ALLOCATION</c>) so the
    /// check applies to the host file or folder. The drive colon is kept.
    /// </summary>
    private static string StripStreams(string path)
    {
        var start = HasDriveRoot(path) ? 2 : 0;
        var parts = path[start..].Split('\\');
        for (var i = 0; i < parts.Length; i++)
        {
            var colon = parts[i].IndexOf(':', StringComparison.Ordinal);
            if (colon >= 0)
            {
                parts[i] = parts[i][..colon];
            }
        }

        return path[..start] + string.Join('\\', parts);
    }

    private static IEnumerable<string> Segments(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        return fullPath[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
    }
}
