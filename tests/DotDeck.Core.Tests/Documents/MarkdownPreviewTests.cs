using DotDeck.Core.Documents;
using DotDeck.Core.Files;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.Documents;

public sealed class MarkdownPreviewTests
{
    private static readonly MarkdownTheme s_theme = new("#1E1E1E", "#DCDCDC", "#999999", "#3F3F46", "#2D2D30", "#3794FF", "Segoe UI", "Consolas");

    [Fact]
    public void Raw_html_and_scripts_are_not_rendered()
    {
        var body = MarkdownPreview.RenderBody("hi <script>alert(1)</script>\n\n<img src=x onerror=alert(1)>");

        Assert.DoesNotContain("<script", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Front_matter_is_hidden_and_gfm_tables_render()
    {
        var body = MarkdownPreview.RenderBody("---\nname: skill\n---\n# Title\n\n| a | b |\n|---|---|\n| 1 | 2 |\n");

        Assert.DoesNotContain("name: skill", body, StringComparison.Ordinal);
        Assert.Contains("<table>", body, StringComparison.Ordinal);
        Assert.Contains("Title", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_carries_csp_base_and_escaped_title()
    {
        var page = MarkdownPreview.BuildPage("x", s_theme, "<a & b>");

        Assert.Contains($"content=\"{MarkdownPreview.ContentSecurityPolicy}\"", page, StringComparison.Ordinal);
        Assert.Contains("<base href=\"https://md.dotdeck.local/\">", page, StringComparison.Ordinal);
        Assert.Contains("<title>&lt;a &amp; b&gt;</title>", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Hostile_theme_value_cannot_break_out_of_css()
    {
        var page = MarkdownPreview.BuildPage("x", s_theme with { Background = "red}</style><script>" }, "t");

        Assert.DoesNotContain("<script>", page, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://app.dotdeck.local/p.html", true)]
    [InlineData("https://md.dotdeck.local/img/a.png", true)]
    [InlineData("https://MD.dotdeck.local/a.png", true)]
    [InlineData("http://md.dotdeck.local/a.png", false)]
    [InlineData("https://md.dotdeck.local:8443/a.png", false)]
    [InlineData("https://example.com/a.png", false)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    [InlineData("https://md.dotdeck.local/.env", false)]
    [InlineData("https://md.dotdeck.local/sub/id_rsa", false)]
    [InlineData("https://md.dotdeck.local/%2Eenv", false)]
    public void Only_virtual_hosts_and_non_secret_files_are_allowed(string url, bool allowed)
    {
        Assert.Equal(allowed, MarkdownPreview.IsAllowedRequest(new Uri(url)));
    }

    [Fact]
    public void Only_real_image_bytes_pass_the_content_check()
    {
        Assert.True(MarkdownPreview.LooksLikeImage([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0], "image/png"));
        Assert.True(MarkdownPreview.LooksLikeImage([0xFF, 0xD8, 0xFF, 0xE0], "image/jpeg"));
        Assert.True(MarkdownPreview.LooksLikeImage("RIFF\0\0\0\0WEBPVP8 "u8, "image/webp"));
        Assert.False(MarkdownPreview.LooksLikeImage("API_KEY=secret"u8, "image/png"));
        Assert.False(MarkdownPreview.LooksLikeImage([0xFF, 0xD8, 0xFF], "image/png"));
        Assert.False(MarkdownPreview.LooksLikeImage([], "image/gif"));
        Assert.False(MarkdownPreview.LooksLikeImage("GIF89a"u8, "text/html"));
    }

    [Theory]
    [InlineData("https://md.dotdeck.local/img/a.png", true)]
    [InlineData("https://md.dotdeck.local/img/A%20b.JPG", true)]
    [InlineData("https://md.dotdeck.local/notes.md", false)]
    [InlineData("https://md.dotdeck.local/.env", false)]
    [InlineData("https://md.dotdeck.local/id_rsa.png", false)]
    [InlineData("https://md.dotdeck.local/missing.png", false)]
    [InlineData("https://md.dotdeck.local/..%2Foutside.png", false)]
    [InlineData("https://md.dotdeck.local/%2E%2E/outside.png", false)]
    [InlineData("https://app.dotdeck.local/img/a.png", false)]
    public void Content_files_are_images_inside_the_folder_only(string url, bool served)
    {
        using var temp = new TempDirectory();
        var folder = temp.Combine("doc");
        Directory.CreateDirectory(System.IO.Path.Combine(folder, "img"));
        foreach (var name in new[] { "img/a.png", "img/A b.JPG", "notes.md", ".env", "id_rsa.png" })
        {
            File.WriteAllBytes(System.IO.Path.Combine(folder, name), [1, 2, 3]);
        }

        File.WriteAllBytes(temp.Combine("outside.png"), [1]);

        var file = MarkdownPreview.ResolveContentFile(PhysicalFileStore.Instance, folder, new Uri(url));

        Assert.Equal(served, file is not null);
        if (file is not null)
        {
            Assert.StartsWith(folder, file.Path, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("image/", file.ContentType, StringComparison.Ordinal);
        }
    }
}
