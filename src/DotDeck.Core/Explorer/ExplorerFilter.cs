using DotDeck.Core.Projects;

namespace DotDeck.Core.Explorer;

/// <summary>Display filter for files (SPEC 2.3 + decision Q6). Folders are always shown.</summary>
public static class ExplorerFilter
{
    // SPEC 2.3 list followed by the Q6 additions.
    private static readonly HashSet<string> s_configDocExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".json", ".jsonc", ".toml", ".yaml", ".yml", ".ps1", ".py", ".cs", ".xaml", ".props", ".txt",
        ".csproj", ".slnx", ".ini", ".xml", ".cmd", ".bat", ".sh", ".psm1",
    };

    public static bool IsFileVisible(string fileName, ExplorerFilterMode mode)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return mode switch
        {
            ExplorerFilterMode.All => true,

            // ".env*" files are configuration too; they stay visible so that their lock icon is seen.
            ExplorerFilterMode.ConfigDocs => FileKindClassifier.IsPlainDotfile(fileName)
                || SecretFilePolicy.IsEnvFile(fileName)
                || s_configDocExtensions.Contains(Path.GetExtension(fileName)),
            _ => true,
        };
    }
}
