using System.Diagnostics;
using System.IO;
using DotDeck.App.Infrastructure.Windows;
using DotDeck.Core.AiCli.Scope;

namespace DotDeck.App.Tests;

public sealed class WindowsFinalPathResolverTests : IDisposable
{
    private readonly string _temp;

    public WindowsFinalPathResolverTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "dotdeck-finalpath-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        // GetTempPath may itself be a short (8.3) name; compare against the resolved form.
        _temp = WindowsFinalPathResolver.Instance.ResolveFinalPath(_temp)!;
    }

    public void Dispose()
    {
        try
        {
            // Remove junctions themselves first so the recursive delete never walks into or trips over them.
            foreach (var dir in Directory.EnumerateDirectories(_temp, "*", SearchOption.AllDirectories)
                         .Where(d => new DirectoryInfo(d).LinkTarget is not null)
                         .ToList())
            {
                Directory.Delete(dir);
            }

            Directory.Delete(_temp, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string Make(params string[] parts)
    {
        var path = Path.Combine([_temp, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static bool TryCreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/J", link, target },
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        process!.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
    }

    [Fact]
    public void Resolves_casing_of_existing_folder()
    {
        var dir = Make("MixedCase 한글");

        var final = WindowsFinalPathResolver.Instance.ResolveFinalPath(dir.ToUpperInvariant());

        Assert.Equal(dir, final);
    }

    [Fact]
    public void Missing_path_does_not_exist_and_does_not_resolve()
    {
        var missing = Path.Combine(_temp, "missing");

        Assert.Equal(PathExistence.Missing, WindowsFinalPathResolver.Instance.Probe(missing));
        Assert.Equal(PathExistence.Missing, WindowsFinalPathResolver.Instance.Probe(Path.Combine(missing, "deeper", "x.txt")));
        Assert.Equal(PathExistence.Exists, WindowsFinalPathResolver.Instance.Probe(_temp));
        Assert.Null(WindowsFinalPathResolver.Instance.ResolveFinalPath(missing));
    }

    [Fact]
    public void Junction_inside_scope_pointing_outside_is_outside()
    {
        var root = Make("scope");
        var outside = Make("outside");
        var link = Path.Combine(root, "link");
        if (!TryCreateJunction(link, outside))
        {
            Assert.Skip("junction creation unavailable");
        }

        var checker = new ScopeChecker([new ScopeRoot(root)], WindowsFinalPathResolver.Instance);

        Assert.Equal(ScopeOutcome.Inside, checker.Check(Path.Combine(root, "a.txt"), root, ScopeAccess.Write).Outcome);
        var decision = checker.Check(Path.Combine(link, "new", "b.txt"), root, ScopeAccess.Write);
        Assert.Equal(ScopeOutcome.Outside, decision.Outcome);
        Assert.Equal(Path.Combine(outside, "new", "b.txt"), decision.FinalPath);
    }

    [Fact]
    public void Dangling_junction_is_unresolved()
    {
        var root = Make("scope");
        var target = Make("gone");
        var link = Path.Combine(root, "dangling");
        if (!TryCreateJunction(link, target))
        {
            Assert.Skip("junction creation unavailable");
        }

        Directory.Delete(target);
        var checker = new ScopeChecker([new ScopeRoot(root)], WindowsFinalPathResolver.Instance);

        Assert.Equal(ScopeOutcome.Unresolved, checker.Check(Path.Combine(link, "x.txt"), root, ScopeAccess.Write).Outcome);
    }

    [Theory]
    [InlineData(@"\\?\C:\x", @"C:\x")]
    [InlineData(@"\\?\UNC\srv\share\x", @"\\srv\share\x")]
    [InlineData(@"C:\x", @"C:\x")]
    public void Converts_extended_paths_to_dos_form(string raw, string expected) =>
        Assert.Equal(expected, WindowsFinalPathResolver.ToDos(raw));
}
