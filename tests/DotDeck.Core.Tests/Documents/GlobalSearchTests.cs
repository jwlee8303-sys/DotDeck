using DotDeck.Core.Documents;
using DotDeck.Core.Explorer;
using DotDeck.Core.Files;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.Documents;

public sealed class GlobalSearchTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Finds_lines_and_skips_secret_binary_and_excluded_files()
    {
        Directory.CreateDirectory(_temp.Combine("node_modules"));
        Directory.CreateDirectory(_temp.Combine("docs"));
        File.WriteAllText(_temp.Combine("a.md"), "first\nhello world\n");
        File.WriteAllText(_temp.Combine("docs", "b.txt"), "﻿say Hello");
        File.WriteAllText(_temp.Combine(".env"), "hello=secret");
        File.WriteAllBytes(_temp.Combine("bin.dat"), [0x68, 0x65, 0x6c, 0x6c, 0x6f, 0x00]);
        File.WriteAllText(_temp.Combine("node_modules", "x.js"), "hello");
        var regex = TextSearch.TryBuild(new SearchOptions("hello"), out _)!;

        var hits = GlobalSearch.Run(PhysicalFileStore.Instance,
            [new SearchRoot("P", _temp.Path, new ExcludedFolderPolicy(ExcludedFolderPolicy.CommonDefaults))], regex, TestContext.Current.CancellationToken).ToList();

        Assert.Equal(2, hits.Count);
        Assert.Equal(("a.md", 2, 1), (hits[0].RelativePath, hits[0].Line, hits[0].Column));
        Assert.Equal((Path.Combine("docs", "b.txt"), 1, 5), (hits[1].RelativePath, hits[1].Line, hits[1].Column));
        Assert.Equal("say Hello", hits[1].LineText);
    }
}
