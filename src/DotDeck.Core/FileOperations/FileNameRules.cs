namespace DotDeck.Core.FileOperations;

/// <summary>Windows file name validation and "복사본" names.</summary>
public static class FileNameRules
{
    public const int MaxNameLength = 255;

    /// <summary>Suffix used for copies, as in Windows Explorer ("a - 복사본.txt").</summary>
    public const string CopySuffix = " - 복사본";

    // Fixed Windows rules, so validation does not depend on the platform the tests run on.
    private static readonly char[] s_invalidChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> s_reservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>Returns a message describing why <paramref name="name"/> cannot be used, or null if it is valid.</summary>
    public static string? Validate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "이름을 입력하세요.";
        }

        if (name.Length > MaxNameLength)
        {
            return $"이름이 너무 깁니다({MaxNameLength}자 이하).";
        }

        foreach (var c in name)
        {
            if (c < 32 || Array.IndexOf(s_invalidChars, c) >= 0)
            {
                return "이름에 다음 문자는 쓸 수 없습니다: < > : \" / \\ | ? * 와 제어 문자";
            }
        }

        // Windows silently strips these, so "a." would become "a" (and "." / ".." are not names).
        if (name.EndsWith('.') || name.EndsWith(' '))
        {
            return "이름은 마침표나 공백으로 끝날 수 없습니다.";
        }

        // "CON", "con.txt" and "NUL .md" all refer to the device.
        var stem = name.Split('.')[0].TrimEnd(' ');
        if (s_reservedNames.Contains(stem))
        {
            return $"'{stem}'은(는) Windows 예약 이름이라 쓸 수 없습니다.";
        }

        return null;
    }

    /// <summary>
    /// Returns the first free name for a copy of <paramref name="name"/>: "a - 복사본.txt", "a - 복사본 (2).txt", …
    /// Folders and dotfiles (".env") keep the whole name as the stem.
    /// </summary>
    public static string MakeCopyName(string name, bool isDirectory, Func<string, bool> isTaken)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(isTaken);

        var extension = isDirectory ? string.Empty : Path.GetExtension(name);
        var stem = name[..^extension.Length];
        if (stem.Length == 0)
        {
            (stem, extension) = (name, string.Empty);
        }

        for (var i = 1; ; i++)
        {
            var suffix = i == 1 ? CopySuffix : $"{CopySuffix} ({i})";

            // Shorten the stem so the copy still fits the name limit. An extension is never cut (that could end
            // the name in a space or half a surrogate pair); if it leaves no room, it is dropped instead.
            var ext = extension;
            var room = MaxNameLength - suffix.Length - ext.Length;
            if (room < 1)
            {
                ext = string.Empty;
                room = MaxNameLength - suffix.Length;
            }

            if (stem.Length > room && char.IsHighSurrogate(stem[room - 1]))
            {
                room--; // never split a surrogate pair
            }

            var shortStem = stem.Length > room ? stem[..room].TrimEnd(' ', '.') : stem;
            if (shortStem.Length == 0)
            {
                shortStem = "_";
            }

            var candidate = $"{shortStem}{suffix}{ext}";
            if (!isTaken(candidate))
            {
                return candidate;
            }
        }
    }
}
