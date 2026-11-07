using System.Text.Json;
using DotDeck.Core.Explorer;
using DotDeck.Core.Files;
using DotDeck.Core.Paths;

namespace DotDeck.Core.SystemConfig;

/// <summary>
/// The "important files" list of the System project (SPEC §8): defaults plus user additions, minus user
/// removals, stored in a JSON file. Secret files can never be added.
/// </summary>
public sealed class ImportantFiles(IFileStore store, string listFile)
{
    private sealed record Stored(List<string> Added, List<string> Removed);

    /// <param name="home">User home (the sandbox home under <c>--home</c>).</param>
    /// <param name="hostsPath">Real hosts file, or a sandbox copy.</param>
    public static IReadOnlyList<string> Defaults(string home, string documents, string localAppData, string hostsPath) =>
    [
        Path.Combine(home, ".gitconfig"),
        Path.Combine(home, ".npmrc"),
        Path.Combine(home, ".ssh", "config"),
        Path.Combine(documents, "PowerShell", "Microsoft.PowerShell_profile.ps1"),
        Path.Combine(documents, "WindowsPowerShell", "Microsoft.PowerShell_profile.ps1"),
        Path.Combine(localAppData, "Packages", "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "LocalState", "settings.json"),
        hostsPath,
    ];

    /// <summary>
    /// Defaults for these paths. In sandbox mode (<c>--home</c>) every default, including hosts, lives under the
    /// sandbox home so manual testing never touches the real files.
    /// </summary>
    public static IReadOnlyList<string> DefaultsFor(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.IsHomeOverridden)
        {
            return Defaults(paths.Home, Path.Combine(paths.Home, "Documents"), Path.Combine(paths.Home, "AppData", "Local"), Path.Combine(paths.Home, "hosts"));
        }

        return Defaults(
            paths.Home,
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.DoNotVerify),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System, Environment.SpecialFolderOption.DoNotVerify), "drivers", "etc", "hosts"));
    }

    public static string ListFileFor(AppPaths paths) => Path.Combine(paths.RoamingDataDir, "important-files.json");

    public IReadOnlyList<string> List(IReadOnlyList<string> defaults)
    {
        var stored = Load();
        return defaults.Where(d => !stored.Removed.Contains(d, StringComparer.OrdinalIgnoreCase))
            .Concat(stored.Added)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Adds a file. Returns an error message (secret file, relative path) or null.</summary>
    public string? Add(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            return "전체 경로를 입력해야 합니다.";
        }

        if (SecretFilePolicy.IsSecret(Path.GetFileName(path)))
        {
            return "비밀 파일은 중요 파일 목록에 추가할 수 없습니다.";
        }

        var stored = Load();
        stored.Removed.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        if (!stored.Added.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            stored.Added.Add(path);
        }

        Save(stored);
        return null;
    }

    public void Remove(string path)
    {
        // Removed hides the path even if it is a default; Added drops it if the user had added it.
        var stored = Load();
        stored.Added.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        if (!stored.Removed.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            stored.Removed.Add(path);
        }

        Save(stored);
    }

    private Stored Load()
    {
        try
        {
            return store.FileExists(listFile) ? JsonSerializer.Deserialize<Stored>(store.ReadAllText(listFile)) ?? new([], []) : new([], []);
        }
        catch (JsonException)
        {
            return new([], []);
        }
    }

    private void Save(Stored stored)
    {
        store.CreateDirectory(Path.GetDirectoryName(listFile)!);
        store.WriteAllTextAtomic(listFile, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
    }
}
