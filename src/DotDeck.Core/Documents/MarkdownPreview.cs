using System.Net;
using System.Text;
using DotDeck.Core.Explorer;
using DotDeck.Core.Files;
using Markdig;

namespace DotDeck.Core.Documents;

/// <summary>Colours of the preview page (CSS colour strings, e.g. "#1E1E1E"), taken from the active theme.</summary>
public sealed record MarkdownTheme(
    string Background,
    string Text,
    string SecondaryText,
    string Border,
    string CodeBackground,
    string Link,
    string FontFamily,
    string CodeFontFamily);

/// <summary>
/// Markdown → offline, script-free HTML page for the preview (DESIGN §3). Raw HTML in the document is not rendered,
/// YAML front matter is hidden, and a CSP allows only inline styles and images from the document's own folder
/// (served by the WebView as <see cref="ContentHost"/>).
/// </summary>
/// <summary>An image the preview may serve from the document folder.</summary>
public sealed record PreviewContentFile(string Path, string ContentType);

public static class MarkdownPreview
{
    /// <summary>Virtual host mapped to the document's folder (images only).</summary>
    public const string ContentHost = "md.dotdeck.local";

    /// <summary>Virtual host mapped to DotDeck's private folder holding the generated pages.</summary>
    public const string PageHost = "app.dotdeck.local";

    public const string ContentSecurityPolicy = "default-src 'none'; img-src https://md.dotdeck.local; style-src 'unsafe-inline'";

    private static readonly MarkdownPipeline s_pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseYamlFrontMatter()
        .DisableHtml()
        .Build();

    public static string RenderBody(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        return Markdown.ToHtml(markdown, s_pipeline);
    }

    public static string BuildPage(string markdown, MarkdownTheme theme, string title)
    {
        ArgumentNullException.ThrowIfNull(theme);
        var t = theme with
        {
            Background = Css(theme.Background),
            Text = Css(theme.Text),
            SecondaryText = Css(theme.SecondaryText),
            Border = Css(theme.Border),
            CodeBackground = Css(theme.CodeBackground),
            Link = Css(theme.Link),
            FontFamily = Css(theme.FontFamily),
            CodeFontFamily = Css(theme.CodeFontFamily),
        };
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\">");
        html.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"").Append(ContentSecurityPolicy).Append("\">");
        html.Append("<base href=\"https://").Append(ContentHost).Append("/\">");
        html.Append("<title>").Append(WebUtility.HtmlEncode(title)).Append("</title><style>");
        html.Append($"html,body{{background:{t.Background};color:{t.Text};}}");
        html.Append($"body{{font-family:{t.FontFamily};font-size:14px;line-height:1.6;margin:0;padding:16px 28px;max-width:980px;}}");
        html.Append($"a{{color:{t.Link};}}");
        html.Append($"h1,h2{{border-bottom:1px solid {t.Border};padding-bottom:.3em;}}");
        html.Append($"code,pre{{font-family:{t.CodeFontFamily};background:{t.CodeBackground};border-radius:3px;}}");
        html.Append("code{padding:.1em .35em;font-size:90%;}pre{padding:12px;overflow:auto;}pre code{padding:0;background:none;}");
        html.Append($"blockquote{{margin:0;padding:0 1em;color:{t.SecondaryText};border-left:.25em solid {t.Border};}}");
        html.Append($"table{{border-collapse:collapse;}}th,td{{border:1px solid {t.Border};padding:4px 10px;}}");
        html.Append($"hr{{border:0;border-top:1px solid {t.Border};}}img{{max-width:100%;}}");
        html.Append("ul.contains-task-list{list-style:none;padding-left:1.2em;}");
        html.Append("</style></head><body>");
        html.Append(RenderBody(markdown));
        html.Append("</body></html>");
        return html.ToString();
    }

    /// <summary>
    /// Whether the preview may load <paramref name="uri"/>: only the two virtual hosts over https, and never a
    /// secret file from the document folder.
    /// </summary>
    public static bool IsAllowedRequest(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort)
        {
            return false;
        }

        if (string.Equals(uri.Host, PageHost, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.Equals(uri.Host, ContentHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = Uri.UnescapeDataString(uri.AbsolutePath.TrimEnd('/').Split('/')[^1]);
        return !SecretFilePolicy.IsSecret(name);
    }

    private static readonly Dictionary<string, string> s_imageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".ico"] = "image/x-icon",
    };

    /// <summary>
    /// Maps a <see cref="ContentHost"/> request to an image file inside <paramref name="folder"/>, or null when it
    /// must be refused: not a raster image, outside the folder, a secret file name, missing, or reached through a
    /// link/junction (whose target could be anything). The preview serves only what this returns.
    /// </summary>
    public static PreviewContentFile? ResolveContentFile(IFileStore store, string? folder, Uri uri)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(uri);
        if (string.IsNullOrEmpty(folder) || !IsAllowedRequest(uri)
            || !string.Equals(uri.Host, ContentHost, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
        if (relative.Length == 0 || relative.IndexOfAny(['\\', ':', '\0']) >= 0)
        {
            return null;
        }

        var root = Path.GetFullPath(folder);
        var rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
            || !s_imageTypes.TryGetValue(Path.GetExtension(full), out var contentType)
            || SecretFilePolicy.IsSecret(Path.GetFileName(full))
            || !store.FileExists(full)
            || store.GetEntry(full) is not { IsReparsePoint: false })
        {
            return null;
        }

        for (var dir = Path.GetDirectoryName(full); dir is not null && dir.Length >= rootWithSeparator.Length; dir = Path.GetDirectoryName(dir))
        {
            if (store.GetEntry(dir) is not { IsReparsePoint: false })
            {
                return null;
            }
        }

        return new PreviewContentFile(full, contentType);
    }

    /// <summary>
    /// Whether <paramref name="bytes"/> really is an image of <paramref name="contentType"/>. Checked on the bytes
    /// actually read, so a file swapped after <see cref="ResolveContentFile"/> or a hard link to a text secret
    /// (e.g. ".env" linked as "a.png") is never served.
    /// </summary>
    public static bool LooksLikeImage(ReadOnlySpan<byte> bytes, string contentType) => contentType switch
    {
        "image/png" => bytes.StartsWith((byte[])[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
        "image/jpeg" => bytes.StartsWith((byte[])[0xFF, 0xD8, 0xFF]),
        "image/gif" => bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8),
        "image/webp" => bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8),
        "image/bmp" => bytes.StartsWith("BM"u8),
        "image/x-icon" => bytes.StartsWith((byte[])[0x00, 0x00, 0x01, 0x00]),
        _ => false,
    };

    // Theme values come from DotDeck's own resources, but never let one break out of its CSS declaration.
    private static string Css(string value) =>
        value.IndexOfAny(['<', '>', '{', '}', ';', '"', '\\']) >= 0 ? "inherit" : value;
}
