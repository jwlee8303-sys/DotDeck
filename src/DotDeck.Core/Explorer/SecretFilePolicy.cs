namespace DotDeck.Core.Explorer;

/// <summary>
/// Decides which files are secret (SPEC 2.3, decision Q15): shown with a lock icon and never opened.
/// Matching is by file name only and case-insensitive.
/// </summary>
public static class SecretFilePolicy
{
    private static readonly string[] s_keyExtensions = [".pfx", ".pem", ".key"];

    private static readonly string[] s_keyPrefixes = ["id_rsa", "id_ed25519"];

    // ".env.example" etc. are templates without real values (Q15).
    private static readonly string[] s_envTemplateSuffixes = [".example", ".sample", ".template"];

    public static bool IsSecret(string fileName)
    {
        var name = Normalize(fileName);
        return (IsEnvName(name) && !IsEnvTemplate(name)) || IsKeyMaterialName(name);
    }

    /// <summary>".env", ".env.local", ".envrc", … (the ".env*" pattern), including templates.</summary>
    public static bool IsEnvFile(string fileName) => IsEnvName(Normalize(fileName));

    /// <summary>Certificates and private keys. "id_rsa.pub" also matches, following the SPEC pattern "id_rsa*".</summary>
    public static bool IsKeyMaterial(string fileName) => IsKeyMaterialName(Normalize(fileName));

    /// <summary>
    /// Reduces a name to the file Windows would actually open: drops an alternate data stream suffix
    /// ("cert.pem::$DATA", "a.key:x") and trailing dots/spaces ("cert.pem. "), which Win32 path handling strips.
    /// </summary>
    internal static string Normalize(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var name = Path.GetFileName(fileName.Replace('/', '\\'));
        var colon = name.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            name = name[..colon];
        }

        return name.TrimEnd('.', ' ');
    }

    private static bool IsEnvName(string name) => name.StartsWith(".env", StringComparison.OrdinalIgnoreCase);

    private static bool IsKeyMaterialName(string fileName)
    {
        foreach (var extension in s_keyExtensions)
        {
            if (fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var prefix in s_keyPrefixes)
        {
            if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsEnvTemplate(string fileName)
    {
        if (!fileName.StartsWith(".env.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var suffix in s_envTemplateSuffixes)
        {
            if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
