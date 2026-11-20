using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotDeck.App.Views.Dialogs;

namespace DotDeck.App.Tests;

/// <summary>THIRD-PARTY-NOTICES.txt ships with the app and names every package the app ships.</summary>
public sealed class ThirdPartyNoticesTests
{
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Packages.props")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("repository root not found");
    }

    [Fact]
    public void Notices_and_icon_license_are_next_to_the_app_and_readable_by_the_about_dialog()
    {
        var text = AboutDialog.ReadNotices(AppContext.BaseDirectory);
        Assert.StartsWith("DotDeck - third-party notices", text, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, AboutDialog.LicensesFolder, "LICENSE-material-icon-theme.txt")));
        Assert.Contains("을 읽지 못했습니다", AboutDialog.ReadNotices(Path.Combine(AppContext.BaseDirectory, "missing")), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_shipped_package_has_a_notice_with_its_exact_version()
    {
        var root = RepoRoot();
        var notices = File.ReadAllText(Path.Combine(root, "THIRD-PARTY-NOTICES.txt"));

        // Packages whose DLLs or native files land in the output, direct or transitive (restore output of the app; the
        // RID-less target is always there, its runtimeTargets cover the native files of a win-x64 publish).
        using var assets = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src", "DotDeck.App", "obj", "project.assets.json")));
        var target = assets.RootElement.GetProperty("targets").EnumerateObject().Single(t => !t.Name.Contains('/', StringComparison.Ordinal));
        var shipped = target.Value.EnumerateObject()
            .Where(p => p.Value.GetProperty("type").GetString() == "package" && ShipsFiles(p.Value))
            .Select(p => p.Name.Split('/'))
            .ToList();

        Assert.Contains(shipped, p => p[0] == "Dirkster.AvalonDock.Core"); // transitive packages are part of the list
        Assert.Contains(shipped, p => p[0] == "Microsoft.Web.WebView2");
        Assert.All(shipped, p => Assert.Matches(new Regex($@"(?m)(^|[ ,]){Regex.Escape(p[0])} {Regex.Escape(p[1])}(?![0-9.])"), notices));
    }

    private static bool ShipsFiles(JsonElement package) =>
        new[] { "runtime", "native", "runtimeTargets" }.Any(kind =>
            package.TryGetProperty(kind, out var files) && files.EnumerateObject().Any(f => !f.Name.EndsWith("/_._", StringComparison.Ordinal)));
}
