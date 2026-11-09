using System.IO;
using System.Reflection;

namespace DotDeck.App.Infrastructure;

/// <summary>
/// The terminal page's files, embedded in DotDeck.exe (<c>Assets/Terminal</c>: xterm.js and its fit add-on, pinned in
/// THIRD-PARTY-NOTICES.txt, plus DotDeck's own page). They are served to WebView2 on the virtual host
/// <see cref="Host"/> only by exact name; nothing is read from disk.
/// </summary>
public static class TerminalAssets
{
    public const string Host = "term.dotdeck.local";

    private const string Prefix = "Terminal/";

    private static readonly Assembly s_assembly = typeof(TerminalAssets).Assembly;

    private static readonly IReadOnlyDictionary<string, string> s_contentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
    };

    // Embedded names use the folder separator of the build machine; requests always use '/'.
    private static readonly IReadOnlyDictionary<string, string> s_resources = s_assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal))
        .ToDictionary(n => n[Prefix.Length..].Replace('\\', '/'), n => n, StringComparer.Ordinal);

    /// <summary>Paths (relative, '/' separated) of every embedded terminal file, including the license texts.</summary>
    public static IReadOnlyCollection<string> Paths => (IReadOnlyCollection<string>)s_resources.Keys;

    /// <summary>Opens an embedded file by its relative path, or returns null.</summary>
    public static Stream? OpenFile(string path) =>
        s_resources.TryGetValue(path, out var name) ? s_assembly.GetManifestResourceStream(name) : null;

    /// <summary>
    /// Resolves a page request: only <c>https://term.dotdeck.local/&lt;embedded path&gt;</c> with a servable type
    /// (html, js, css). Query strings are ignored; anything else (other hosts, schemes, unknown paths) is null.
    /// </summary>
    public static (Stream Content, string ContentType)? Open(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        var path = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        if (!s_contentTypes.TryGetValue(Path.GetExtension(path), out var type) || OpenFile(path) is not { } stream)
        {
            return null;
        }

        return (stream, type);
    }
}
