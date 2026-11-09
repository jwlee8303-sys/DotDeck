using System.Text.Json;
using DotDeck.Core.Files;
using DotDeck.Core.Toml;

namespace DotDeck.Core.AiCli.Launch;

public static class CodexSandboxPolicy
{
    /// <summary>Reads only config.toml, never credentials. Unknown or malformed config fails closed.</summary>
    public static string? ReadMode(IFileStore store, string codexHome)
    {
        var path = Path.Combine(codexHome, "config.toml");
        try
        {
            if (!store.FileExists(path) || store.GetEntry(path)?.Length > 1024 * 1024
                || DotDeck.Core.Explorer.SecretFileResolver.IsSecretFile(store, path)) return null;
            var document = TomlEditDocument.Parse(store.ReadAllBytes(path));
            var entry = document.Entries.FirstOrDefault(e => e.Path.SequenceEqual(new[] { "windows", "sandbox" }));
            if (entry is null) return null;
            var literal = entry.Literal.Trim();
            var mode = literal.StartsWith('\'') && literal.EndsWith('\'') ? literal[1..^1] : JsonSerializer.Deserialize<string>(literal);
            return mode is "elevated" or "unelevated" or "mxc" ? mode : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TomlEditException or JsonException or ArgumentException)
        {
            return null;
        }
    }
}
