using DotDeck.Core.AiCli.Scope;

namespace DotDeck.Core.Tests.AiCli;

/// <summary>Fake resolver: a set of existing paths plus link mappings (link path → target), resolved case-insensitively.</summary>
internal sealed class FakeFinalPathResolver : IFinalPathResolver
{
    private readonly HashSet<string> _existing = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _links = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _canonical = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _broken = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers an existing path; its spelling is the on-disk casing.</summary>
    public FakeFinalPathResolver Add(params string[] paths)
    {
        foreach (var path in paths)
        {
            var current = path;
            while (current is not null)
            {
                _existing.Add(current);
                _canonical.TryAdd(current, current);
                current = Path.GetDirectoryName(current);
            }
        }

        return this;
    }

    /// <summary>Registers a link or junction (or an 8.3 alias) at <paramref name="link"/> pointing to <paramref name="target"/>.</summary>
    public FakeFinalPathResolver Link(string link, string target)
    {
        Add(Path.GetDirectoryName(link)!);
        _existing.Add(link);
        _links[link] = target;
        return this;
    }

    public FakeFinalPathResolver Broken(string path)
    {
        _existing.Add(path);
        _broken.Add(path);
        return this;
    }

    private readonly HashSet<string> _inaccessible = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers an entry whose attributes cannot be read (access denied).</summary>
    public FakeFinalPathResolver Inaccessible(string path)
    {
        Add(Path.GetDirectoryName(path)!);
        _inaccessible.Add(path);
        return this;
    }

    public PathExistence Probe(string fullPath)
    {
        var path = PathNormalizer.TrimEnd(fullPath);
        if (_inaccessible.Contains(path))
        {
            return PathExistence.Unknown;
        }

        return _existing.Contains(path) ? PathExistence.Exists : PathExistence.Missing;
    }

    public string? ResolveFinalPath(string fullPath)
    {
        var path = PathNormalizer.TrimEnd(fullPath);
        for (var hops = 0; hops < 40; hops++)
        {
            if (_broken.Contains(path))
            {
                return null;
            }

            // Follow the longest link prefix.
            var link = _links.Keys
                .Where(k => PathNormalizer.IsSameOrUnder(path, k))
                .OrderByDescending(k => k.Length)
                .FirstOrDefault();
            if (link is null)
            {
                return _canonical.TryGetValue(path, out var canonical) ? canonical : path;
            }

            path = PathNormalizer.TrimEnd(_links[link] + path[link.Length..]);
        }

        return null;
    }
}

public sealed class PathNormalizerTests
{
    private const string Cwd = @"C:\work\proj";

    [Theory]
    [InlineData(@"C:\work\proj\a.txt", @"C:\work\proj\a.txt")]
    [InlineData(@"  ""C:\work\proj\a b.txt""  ", @"C:\work\proj\a b.txt")]
    [InlineData(@"'C:\work\proj\a.txt'", @"C:\work\proj\a.txt")]
    [InlineData(@"C:/work/proj/a.txt", @"C:\work\proj\a.txt")]
    [InlineData(@"a.txt", @"C:\work\proj\a.txt")]
    [InlineData(@".\src\..\a.txt", @"C:\work\proj\a.txt")]
    [InlineData(@"..\..\other\a.txt", @"C:\other\a.txt")]
    [InlineData(@"\top\a.txt", @"C:\top\a.txt")]
    [InlineData(@"C:sub\a.txt", @"C:\work\proj\sub\a.txt")]
    [InlineData(@"C:\work\proj\dir\", @"C:\work\proj\dir")]
    [InlineData(@"\\?\C:\work\proj\a.txt", @"C:\work\proj\a.txt")]
    [InlineData(@"\??\C:\work\proj\a.txt", @"C:\work\proj\a.txt")]
    [InlineData(@"\\?\UNC\srv\share\a.txt", @"\\srv\share\a.txt")]
    [InlineData(@"\\srv\share\dir\..\a.txt", @"\\srv\share\a.txt")]
    [InlineData(@"C:\work\proj\a.txt:hidden", @"C:\work\proj\a.txt")]
    [InlineData(@"C:\work\proj\a.txt::$DATA", @"C:\work\proj\a.txt")]
    [InlineData(@"C:\work\proj\dir::$INDEX_ALLOCATION\x.txt", @"C:\work\proj\dir\x.txt")]
    [InlineData(@"~\notes.md", @"C:\Users\me\notes.md")]
    [InlineData(@"C:\work\proj\한글 폴더\파일.md", @"C:\work\proj\한글 폴더\파일.md")]
    public void Normalizes_to_full_path(string raw, string expected)
    {
        var result = PathNormalizer.Normalize(raw, Cwd, @"C:\Users\me");

        Assert.Equal(NormalizedPathKind.Path, result.Kind);
        Assert.Equal(expected, result.FullPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\.\pipe\x")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume1\x")]
    [InlineData(@"\\?\Volume{12345678-1234-1234-1234-123456789012}\x")]
    [InlineData(@"C:\work\proj\con")]
    [InlineData(@"C:\work\proj\COM1.txt")]
    [InlineData(@"C:\work\proj\lpt1\x.txt")]
    [InlineData(@"D:relative.txt")]
    [InlineData("a\0b")]
    public void Rejects_unverifiable_paths(string raw)
    {
        Assert.Equal(NormalizedPathKind.Invalid, PathNormalizer.Normalize(raw, Cwd, @"C:\Users\me").Kind);
    }

    [Theory]
    [InlineData("NUL")]
    [InlineData(@"C:\work\proj\nul")]
    [InlineData(@"\\.\NUL")]
    public void Recognizes_null_device(string raw)
    {
        Assert.Equal(NormalizedPathKind.NullDevice, PathNormalizer.Normalize(raw, Cwd).Kind);
    }

    [Fact]
    public void Relative_path_without_cwd_is_rejected()
    {
        Assert.Equal(NormalizedPathKind.Invalid, PathNormalizer.Normalize("a.txt", cwd: null).Kind);
    }

    [Fact]
    public void Home_path_without_home_is_rejected()
    {
        Assert.Equal(NormalizedPathKind.Invalid, PathNormalizer.Normalize(@"~\a.txt", Cwd, home: null).Kind);
    }

    [Theory]
    [InlineData(@"C:\work", @"C:\work", true)]
    [InlineData(@"C:\WORK\a", @"C:\work", true)]
    [InlineData(@"C:\work2", @"C:\work", false)]
    [InlineData(@"C:\work\", @"C:\work", true)]
    [InlineData(@"C:\x", @"C:\", true)]
    [InlineData(@"D:\x", @"C:\", false)]
    [InlineData(@"\\srv\share\a", @"\\SRV\Share", true)]
    [InlineData(@"\\srv\share2", @"\\srv\share", false)]
    public void Same_or_under_compares_whole_segments(string path, string root, bool expected) =>
        Assert.Equal(expected, PathNormalizer.IsSameOrUnder(path, root));
}

public sealed class ScopeCheckerTests
{
    private const string Root = @"C:\work\proj";
    private const string Other = @"D:\web game";

    private static ScopeChecker Checker(FakeFinalPathResolver resolver, params ScopeRoot[] roots) =>
        new(roots.Length == 0 ? [new ScopeRoot(Root), new ScopeRoot(Other)] : roots, resolver);

    private static FakeFinalPathResolver Disk() => new FakeFinalPathResolver().Add(Root, Other, @"C:\work\proj2", @"C:\elsewhere");

    [Theory]
    [InlineData(@"C:\work\proj\a.txt")]
    [InlineData(@"c:\WORK\PROJ\new\deep\file.md")]
    [InlineData(@"D:\web game\README.md")]
    [InlineData(@"src\x.cs")]
    [InlineData(@"C:\work\proj")]
    public void Paths_inside_the_scope_are_inside(string raw)
    {
        var decision = Checker(Disk()).Check(raw, Root, ScopeAccess.Write);

        Assert.Equal(ScopeOutcome.Inside, decision.Outcome);
    }

    [Theory]
    [InlineData(@"C:\work\proj2\a.txt")]
    [InlineData(@"C:\work\a.txt")]
    [InlineData(@"..\proj2\a.txt")]
    [InlineData(@"C:\work\proj\..\..\elsewhere\a.txt")]
    [InlineData(@"\\srv\share\a.txt")]
    [InlineData(@"\\?\UNC\srv\share\a.txt")]
    [InlineData(@"E:\a.txt")]
    public void Paths_outside_the_scope_are_outside_or_unresolved(string raw)
    {
        var decision = Checker(Disk().Add(@"\\srv\share", @"E:\")).Check(raw, Root, ScopeAccess.Write);

        Assert.Equal(ScopeOutcome.Outside, decision.Outcome);
        Assert.Contains("범위 밖", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Junction_inside_scope_pointing_outside_is_outside()
    {
        var resolver = Disk().Link(@"C:\work\proj\link", @"C:\elsewhere");

        var decision = Checker(resolver).Check(@"C:\work\proj\link\secret.txt", Root, ScopeAccess.Write);

        Assert.Equal(ScopeOutcome.Outside, decision.Outcome);
        Assert.Equal(@"C:\elsewhere\secret.txt", decision.FinalPath);
    }

    [Fact]
    public void Link_chain_is_followed_to_the_final_target()
    {
        var resolver = Disk()
            .Link(@"C:\work\proj\a", @"C:\work\proj\b")
            .Link(@"C:\work\proj\b", @"C:\elsewhere");

        Assert.Equal(ScopeOutcome.Outside, Checker(resolver).Check(@"C:\work\proj\a\x", Root, ScopeAccess.Read).Outcome);
    }

    [Fact]
    public void Link_outside_pointing_into_the_scope_is_inside()
    {
        var resolver = Disk().Link(@"C:\elsewhere\shortcut", Root);

        Assert.Equal(ScopeOutcome.Inside, Checker(resolver).Check(@"C:\elsewhere\shortcut\a.txt", Root, ScopeAccess.Write).Outcome);
    }

    [Fact]
    public void Root_reached_through_a_link_accepts_its_final_paths()
    {
        var resolver = Disk().Link(@"C:\work\proj\link", @"C:\elsewhere");

        var checker = Checker(resolver, new ScopeRoot(@"C:\work\proj\link"));

        Assert.Equal([@"C:\elsewhere"], checker.FinalRoots);
        Assert.Equal(ScopeOutcome.Inside, checker.Check(@"C:\elsewhere\a.txt", Root, ScopeAccess.Write).Outcome);
        Assert.Equal(ScopeOutcome.Inside, checker.Check(@"C:\work\proj\link\a.txt", Root, ScopeAccess.Write).Outcome);
        Assert.Equal(ScopeOutcome.Outside, checker.Check(@"C:\work\proj\a.txt", Root, ScopeAccess.Write).Outcome);
    }

    [Fact]
    public void Short_names_are_expanded_through_the_resolver()
    {
        // PROGRA~1 behaves like a link to the long name from the checker's point of view.
        var resolver = Disk().Add(@"C:\Program Files").Link(@"C:\PROGRA~1", @"C:\Program Files");

        var checker = Checker(resolver, new ScopeRoot(@"C:\Program Files"));

        Assert.Equal(ScopeOutcome.Inside, checker.Check(@"C:\PROGRA~1\x.txt", Root, ScopeAccess.Write).Outcome);
        Assert.Equal(ScopeOutcome.Outside, Checker(resolver).Check(@"C:\PROGRA~1\x.txt", Root, ScopeAccess.Write).Outcome);
    }

    [Fact]
    public void Broken_link_is_unresolved()
    {
        var resolver = Disk().Broken(@"C:\work\proj\dangling");

        var decision = Checker(resolver).Check(@"C:\work\proj\dangling\x", Root, ScopeAccess.Write);

        Assert.Equal(ScopeOutcome.Unresolved, decision.Outcome);
    }

    [Fact]
    public void Missing_volume_is_unresolved()
    {
        Assert.Equal(ScopeOutcome.Unresolved, Checker(Disk()).Check(@"Q:\a.txt", Root, ScopeAccess.Write).Outcome);
    }

    [Theory]
    [InlineData(@"\\.\pipe\x")]
    [InlineData(@"C:\work\proj\con")]
    [InlineData("")]
    public void Invalid_paths_are_unresolved(string raw)
    {
        Assert.Equal(ScopeOutcome.Unresolved, Checker(Disk()).Check(raw, Root, ScopeAccess.Write).Outcome);
    }

    [Fact]
    public void Null_device_is_inside()
    {
        Assert.Equal(ScopeOutcome.Inside, Checker(Disk()).Check("NUL", Root, ScopeAccess.Write).Outcome);
    }

    [Fact]
    public void Read_only_root_allows_reads_and_denies_writes()
    {
        var resolver = Disk().Add(@"C:\Users\me\.claude\skills");
        var checker = Checker(resolver, new ScopeRoot(Root), new ScopeRoot(@"C:\Users\me\.claude\skills", ReadOnly: true));

        Assert.Equal(ScopeOutcome.Inside, checker.Check(@"C:\Users\me\.claude\skills\a\SKILL.md", Root, ScopeAccess.Read).Outcome);
        var write = checker.Check(@"C:\Users\me\.claude\skills\a\SKILL.md", Root, ScopeAccess.Write);
        Assert.Equal(ScopeOutcome.Outside, write.Outcome);
        Assert.Contains("읽기 전용", write.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Narrowed_scope_denies_removed_root_immediately()
    {
        var resolver = Disk();
        var before = Checker(resolver);
        var after = Checker(resolver, new ScopeRoot(Root));

        Assert.Equal(ScopeOutcome.Inside, before.Check(@"D:\web game\a.txt", Root, ScopeAccess.Write).Outcome);
        Assert.Equal(ScopeOutcome.Outside, after.Check(@"D:\web game\a.txt", Root, ScopeAccess.Write).Outcome);
    }

    [Fact]
    public void Invalid_roots_grant_nothing()
    {
        var checker = new ScopeChecker([new ScopeRoot(""), new ScopeRoot(@"\\.\pipe\x"), new ScopeRoot("relative")], Disk());

        Assert.Empty(checker.FinalRoots);
        Assert.Equal(ScopeOutcome.Outside, checker.Check(@"C:\work\proj\a.txt", Root, ScopeAccess.Read).Outcome);
    }

    [Fact]
    public void Inaccessible_entry_is_unresolved_instead_of_walked_past()
    {
        var resolver = Disk().Inaccessible(@"C:\work\proj\locked");

        var decision = Checker(resolver).Check(@"C:\work\proj\locked\a.txt", Root, ScopeAccess.Write);

        Assert.Equal(ScopeOutcome.Unresolved, decision.Outcome);
    }

    [Fact]
    public void Root_that_cannot_be_resolved_grants_nothing()
    {
        var resolver = Disk().Broken(@"C:\work\proj\link");

        var checker = Checker(resolver, new ScopeRoot(@"C:\work\proj\link"));

        Assert.Empty(checker.FinalRoots);
        Assert.NotEqual(ScopeOutcome.Inside, checker.Check(@"C:\work\proj\link\a.txt", Root, ScopeAccess.Write).Outcome);
    }

    [Theory]
    [InlineData(@"\\?\C:\elsewhere\NUL.txt")]
    [InlineData(@"\\?\C:\work\proj\nul")]
    [InlineData(@"\\?\C:\work\proj\a.")]
    [InlineData(@"\\?\C:\work\proj\a ")]
    [InlineData(@"\\?\C:\work\proj\..\proj2\a")]
    [InlineData(@"\\?\C:\work\proj\.\a")]
    [InlineData(@"\\?\C:\work\proj\\a")]
    [InlineData(@"\\?\C:/work/proj/a")]
    [InlineData(@"\\?\UNC\srv\share\con")]
    [InlineData(@"\\?\C:")]
    [InlineData(@"\??\C:")]
    [InlineData(@"\\?\C:proj")]
    public void Extended_paths_with_literal_special_names_are_unresolved(string raw)
    {
        Assert.Equal(ScopeOutcome.Unresolved, Checker(Disk()).Check(raw, Root, ScopeAccess.Write).Outcome);
    }

    [Fact]
    public void Plain_extended_path_inside_scope_is_inside()
    {
        Assert.Equal(ScopeOutcome.Inside, Checker(Disk()).Check(@"\\?\C:\work\proj\src\a.txt", Root, ScopeAccess.Write).Outcome);
        Assert.Equal(ScopeOutcome.Inside, Checker(Disk()).Check(@"\\?\C:\work\proj\dir\", Root, ScopeAccess.Write).Outcome);
    }

    [Fact]
    public void Real_junction_with_lexical_resolver_is_documented_as_insufficient()
    {
        // The lexical resolver performs no link resolution; production code must use the Windows resolver.
        Assert.Equal(@"C:\a\link", LexicalPathResolver.Instance.ResolveFinalPath(@"C:\a\link"));
    }
}
