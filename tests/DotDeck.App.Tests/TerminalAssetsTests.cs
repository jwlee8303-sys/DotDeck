using System.IO;
using System.Security.Cryptography;
using DotDeck.App.Infrastructure;

namespace DotDeck.App.Tests;

public sealed class TerminalAssetsTests
{
    // Pinned in THIRD-PARTY-NOTICES.txt and docs/DESIGN.md (@xterm/xterm 6.0.0, @xterm/addon-fit 0.11.0, unmodified).
    public static TheoryData<string, string> Pinned => new()
    {
        { "xterm/xterm.js", "14903579ff54664cd72f8e8699e6961a6272c21863ec1c3b118cdc8af5d4a972" },
        { "xterm/xterm.css", "854a7c0fb70e8b1a083c16797ab827299fb18744f5ad34f227b48337e33293c6" },
        { "addon-fit/addon-fit.js", "ba3ea256ce0620a0992a197d6c9baea64823fc93d8da07a9e366ca9943c18527" },
        { "xterm/LICENSE", "b569f629d00f2626a8100df2a1798210535621e42164dfd426a6fe5aac7b0ccd" },
        { "addon-fit/LICENSE", "e256f01188af527e4d06d21d06fbf785ae9c50d4b328bf03cbe0ba7f0aa4228f" },
    };

    [Theory]
    [MemberData(nameof(Pinned))]
    public void Bundled_files_are_embedded_unmodified(string path, string sha256)
    {
        using var stream = TerminalAssets.OpenFile(path);
        Assert.NotNull(stream);

        Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(stream)));
    }

    [Theory]
    [MemberData(nameof(Pinned))]
    public void Notices_list_every_pinned_hash(string path, string sha256)
    {
        var notices = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt"));

        if (!path.EndsWith("LICENSE", StringComparison.Ordinal))
        {
            Assert.Contains($"{path}  {sha256}", notices, StringComparison.Ordinal);
        }

        Assert.Contains("Permission is hereby granted, free of charge", notices, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://term.dotdeck.local/xterm/xterm.js", "text/javascript; charset=utf-8")]
    [InlineData("https://TERM.dotdeck.local/xterm/xterm.css?v=2", "text/css; charset=utf-8")]
    [InlineData("https://term.dotdeck.local/addon-fit/addon-fit.js", "text/javascript; charset=utf-8")]
    [InlineData("https://term.dotdeck.local/terminal.html", "text/html; charset=utf-8")]
    [InlineData("https://term.dotdeck.local/terminal.js", "text/javascript; charset=utf-8")]
    [InlineData("https://term.dotdeck.local/terminal.css", "text/css; charset=utf-8")]
    public void Page_files_are_served_by_exact_name(string url, string type)
    {
        var served = TerminalAssets.Open(new Uri(url));

        Assert.NotNull(served);
        using var content = served.Value.Content;
        Assert.Equal(type, served.Value.ContentType);
        Assert.True(content.Length > 0);
    }

    [Theory]
    [InlineData("http://term.dotdeck.local/xterm/xterm.js")]
    [InlineData("https://term.dotdeck.local:8443/xterm/xterm.js")]
    [InlineData("https://user@term.dotdeck.local/xterm/xterm.js")]
    [InlineData("https://other.local/xterm/xterm.js")]
    [InlineData("https://term.dotdeck.local.evil.example/xterm/xterm.js")]
    [InlineData("https://term.dotdeck.local/xterm/LICENSE")]
    [InlineData("https://term.dotdeck.local/xterm/xterm.js.map")]
    [InlineData("https://term.dotdeck.local/xterm/../xterm/xterm.js.map")]
    [InlineData("https://term.dotdeck.local/xterm%2Fxterm.js.map")]
    [InlineData("https://term.dotdeck.local/missing.js")]
    [InlineData("https://term.dotdeck.local/")]
    [InlineData("file:///C:/Windows/win.ini")]
    public void Anything_else_is_refused(string url)
    {
        Assert.Null(TerminalAssets.Open(new Uri(url)));
    }

    [Fact]
    public void Licenses_are_embedded_next_to_their_code()
    {
        Assert.Contains("xterm/LICENSE", TerminalAssets.Paths);
        Assert.Contains("addon-fit/LICENSE", TerminalAssets.Paths);
    }
}
