namespace DotDeck.Core.Explorer;

/// <summary>File type used for the tree icon (and later for the editor's syntax highlighting).</summary>
public enum FileKind
{
    Other,
    Markdown,
    Json,
    Toml,
    Yaml,
    PowerShell,
    Python,
    CSharp,
    Xaml,
    MsBuild,
    Solution,
    Xml,
    Ini,
    Script,
    Text,

    /// <summary>Extension-less dotfiles such as ".gitignore", ".editorconfig", and ".env*" files.</summary>
    DotConfig,

    /// <summary>Certificates and private keys.</summary>
    KeyMaterial,
}

public static class FileKindClassifier
{
    private static readonly Dictionary<string, FileKind> s_byExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".md"] = FileKind.Markdown,
        [".markdown"] = FileKind.Markdown,
        [".json"] = FileKind.Json,
        [".jsonc"] = FileKind.Json,
        [".toml"] = FileKind.Toml,
        [".yaml"] = FileKind.Yaml,
        [".yml"] = FileKind.Yaml,
        [".ps1"] = FileKind.PowerShell,
        [".psm1"] = FileKind.PowerShell,
        [".psd1"] = FileKind.PowerShell,
        [".py"] = FileKind.Python,
        [".cs"] = FileKind.CSharp,
        [".xaml"] = FileKind.Xaml,
        [".props"] = FileKind.MsBuild,
        [".targets"] = FileKind.MsBuild,
        [".csproj"] = FileKind.MsBuild,
        [".sln"] = FileKind.Solution,
        [".slnx"] = FileKind.Solution,
        [".xml"] = FileKind.Xml,
        [".ini"] = FileKind.Ini,
        [".cmd"] = FileKind.Script,
        [".bat"] = FileKind.Script,
        [".sh"] = FileKind.Script,
        [".txt"] = FileKind.Text,
    };

    public static FileKind Classify(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        if (SecretFilePolicy.IsKeyMaterial(fileName))
        {
            return FileKind.KeyMaterial;
        }

        if (SecretFilePolicy.IsEnvFile(fileName) || IsPlainDotfile(fileName))
        {
            return FileKind.DotConfig;
        }

        return s_byExtension.GetValueOrDefault(Path.GetExtension(fileName), FileKind.Other);
    }

    /// <summary>A name that starts with '.' and has no other '.' (".gitignore", ".npmrc"; not ".eslintrc.json").</summary>
    public static bool IsPlainDotfile(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return fileName.Length > 1 && fileName[0] == '.' && fileName.IndexOf('.', 1) < 0;
    }
}
