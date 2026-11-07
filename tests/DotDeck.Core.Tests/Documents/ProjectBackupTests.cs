using System.IO.Compression;
using DotDeck.Core.Documents;
using DotDeck.Core.Explorer;
using DotDeck.Core.Files;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.Documents;

public sealed class ProjectBackupTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 8, 9, 0, 0, TimeSpan.Zero));

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Zips_selected_projects_without_secrets_or_excluded_folders()
    {
        var a = _temp.Combine("a");
        Directory.CreateDirectory(Path.Combine(a, "agents"));
        Directory.CreateDirectory(Path.Combine(a, "node_modules"));
        File.WriteAllText(Path.Combine(a, "CLAUDE.md"), "hi");
        File.WriteAllText(Path.Combine(a, "agents", "x.md"), "x");
        File.WriteAllText(Path.Combine(a, ".env"), "SECRET=1");
        File.WriteAllText(Path.Combine(a, "node_modules", "big.js"), "y");
        var policy = new ExcludedFolderPolicy(ExcludedFolderPolicy.CommonDefaults);

        var result = ProjectBackup.CreateZip(PhysicalFileStore.Instance,
            [new SearchRoot("Claude: 전역", a, policy), new SearchRoot("Claude: 전역", a, policy)],
            _temp.Combine("out"), _time, TestContext.Current.CancellationToken);

        Assert.Equal(4, result.FileCount);
        Assert.Equal(2, result.SkippedSecrets);
        using var zip = ZipFile.OpenRead(result.ZipPath);
        Assert.Equal(
            ["Claude_ 전역 (2)/CLAUDE.md", "Claude_ 전역 (2)/agents/x.md", "Claude_ 전역/CLAUDE.md", "Claude_ 전역/agents/x.md"],
            zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void Relative_destination_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => ProjectBackup.CreateZip(PhysicalFileStore.Instance, [], "rel", _time, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Destination_inside_a_project_and_same_second_runs_work()
    {
        var a = _temp.Combine("a");
        Directory.CreateDirectory(a);
        File.WriteAllText(Path.Combine(a, "x.md"), "x");
        var root = new SearchRoot("A", a, ExcludedFolderPolicy.None);

        var first = ProjectBackup.CreateZip(PhysicalFileStore.Instance, [root], a, _time, TestContext.Current.CancellationToken);
        var second = ProjectBackup.CreateZip(PhysicalFileStore.Instance, [root], a, _time, TestContext.Current.CancellationToken);

        Assert.NotEqual(first.ZipPath, second.ZipPath);
        Assert.True(File.Exists(first.ZipPath));
        Assert.Equal(2, second.FileCount); // x.md and the first zip, never itself
    }
}
