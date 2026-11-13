namespace DotDeck.Installer;

internal static class Paths
{
    /// <summary>A full path on a local drive (X:\...). Machine-wide installs do not support network folders.</summary>
    public static bool IsLocalFullPath(string path) =>
        path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');

    public static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');

    public static bool SameFolder(string a, string b)
    {
        try
        {
            return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or inside it.</summary>
    public static bool IsInside(string path, string folder)
    {
        var p = Normalize(path) + "\\";
        var f = Normalize(folder) + "\\";
        return p.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>DotDeck's settings and data (%APPDATA%\DotDeck, %LOCALAPPDATA%\DotDeck): never an install folder.</summary>
    public static IEnumerable<string> DataFolders(string packId) =>
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), packId),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), packId),
    ];

    public static string DefaultMachineFolder(string packId) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), packId);
}
