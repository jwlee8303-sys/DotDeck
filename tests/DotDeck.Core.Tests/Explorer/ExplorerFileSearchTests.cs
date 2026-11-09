using DotDeck.Core.Explorer;
using DotDeck.Core.Explorer.Visibility;
using DotDeck.Core.Files;
using DotDeck.Core.Projects;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.Explorer;

public sealed class FileNameFilterTests
{
    private static NodeInfo File(string relative) => new()
    {
        Id = relative,
        Kind = NodeKind.File,
        Name = Path.GetFileName(relative),
        RelativePath = relative,
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" ; , ")]
    public void Empty_query_means_no_filter(string? text) => Assert.Null(FileNameFilter.Parse(text));

    [Theory]
    [InlineData("*.md", @"docs\README.md", true)]
    [InlineData("*.md", @"docs\README.MD", true)]
    [InlineData("*.md", @"docs\notes.mdx", false)]
    [InlineData("read", @"docs\README.md", true)]
    [InlineData("docs", @"docs\README.md", false)]
    [InlineData("a?.json", "ab.json", true)]
    [InlineData("a?.json", "abc.json", false)]
    [InlineData("*.md; *.json", "x.json", true)]
    [InlineData("*.md, *.toml", "x.json", false)]
    [InlineData("(1)", "file (1).txt", true)]
    [InlineData("[x]*", "[x]a.txt", true)]
    public void Name_terms_match_the_file_name(string query, string path, bool expected) =>
        Assert.Equal(expected, FileNameFilter.Parse(query)!.IsMatch(File(path)));

    [Theory]
    [InlineData(@"agents\*.md", @"agents\a.md", true)]
    [InlineData("agents/*.md", @".claude\agents\a.md", true)]
    [InlineData(@"agents\*.md", @"agents\deep\a.md", false)]
    [InlineData(@"agents\*.md", @"myagents\a.md", false)]
    [InlineData(@"docs\", @"docs\a.md", true)]
    [InlineData(@"docs\", @"src\a.md", false)]
    public void Path_terms_match_the_relative_path(string query, string path, bool expected) =>
        Assert.Equal(expected, FileNameFilter.Parse(query)!.IsMatch(File(path)));
}

public sealed class ExplorerFileSearchTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private ProjectDefinition Project(ProjectViewSettings? view = null) =>
        new() { Id = "p1", Kind = ProjectKind.Folder, Name = "P", Root = _temp.Path, View = view ?? ProjectViewSettings.Default };

    private FolderProjectSource All(ProjectDefinition? project = null) => new(project ?? Project(), _temp.Path, PhysicalFileStore.Instance);

    private void Touch(params string[] parts)
    {
        var path = _temp.Combine(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, "x");
    }

    private static string Describe(NodeInfo node) => node.FlatDirectory is { Length: > 0 } d ? $@"{d}\{node.Name}" : node.Name;

    [Fact]
    public void Finds_matching_files_at_any_depth_sorted_by_name()
    {
        Touch("docs", "guide", "b.md");
        Touch("docs", "a.md");
        Touch("src", "x.cs");
        Touch("top.md");

        var result = ExplorerFileSearch.Find(All(), FileNameFilter.Parse("*.md")!, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([@"docs\a.md", @"docs\guide\b.md", "top.md"], result.Files.Select(Describe));
        Assert.False(result.IsTruncated);
        Assert.All(result.Files, f => Assert.Equal(NodeKind.File, f.Kind));
    }

    [Fact]
    public void Normal_mode_searches_only_visible_files()
    {
        Touch("docs", "a.md");
        Touch("docs", "hidden.md");
        Touch("notes.log");
        var settings = new VisibilitySettings
        {
            Rules = VisibilitySettings.CreateRules([new(@"docs\hidden.md", VisibilityRule.Exclude)]),
        };
        var project = Project();
        var visible = new VisibleProjectSource(All(project), PhysicalFileStore.Instance, new VisibilityEvaluator(settings, [".md"]), project.View);

        var md = ExplorerFileSearch.Find(visible, FileNameFilter.Parse("*.md")!, cancellationToken: TestContext.Current.CancellationToken);
        var log = ExplorerFileSearch.Find(visible, FileNameFilter.Parse("notes")!, cancellationToken: TestContext.Current.CancellationToken);
        var everything = ExplorerFileSearch.Find(All(project), FileNameFilter.Parse("*")!, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([@"docs\a.md"], md.Files.Select(Describe));
        Assert.Empty(log.Files);
        Assert.Equal(3, everything.Files.Count);
    }

    [Theory]
    [InlineData(ExplorerViewMode.Tree)]
    [InlineData(ExplorerViewMode.Compact)]
    [InlineData(ExplorerViewMode.Flat)]
    public void Every_layout_finds_the_same_files_once(ExplorerViewMode mode)
    {
        Touch("a", "b", "c", "deep.md");
        Touch("pinned.md");
        Touch("x", "y.md");
        var view = ProjectViewSettings.Default with { Mode = mode, Pins = ["pinned.md", "a"] };
        var project = Project(view);
        var visible = new VisibleProjectSource(All(project), PhysicalFileStore.Instance, new VisibilityEvaluator(VisibilitySettings.Default, [".md"]), view);

        var result = ExplorerFileSearch.Find(visible, FileNameFilter.Parse(".md")!, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([@"a\b\c\deep.md", "pinned.md", @"x\y.md"], result.Files.Select(Describe));
        Assert.All(result.Files, f => Assert.False(f.IsPinned));
        Assert.All(result.Files, f => Assert.Null(f.Chain));
    }

    [Fact]
    public void Custom_files_are_searched_under_their_node()
    {
        Touch("in.md");
        using var elsewhere = new TempDirectory();
        var outside = elsewhere.Combine("custom.md");
        System.IO.File.WriteAllText(outside, "x");
        var source = new CustomFilesProjectSource(All(), PhysicalFileStore.Instance, [outside]);

        var result = ExplorerFileSearch.Find(source, FileNameFilter.Parse("custom")!, cancellationToken: TestContext.Current.CancellationToken);

        var file = Assert.Single(result.Files);
        Assert.True(file.IsCustomFile);
        Assert.Equal(CustomFileRules.NodeName, file.FlatDirectory);
    }

    [Fact]
    public void Result_limit_marks_the_result_truncated()
    {
        for (var i = 0; i < 5; i++)
        {
            Touch($"f{i}.md");
        }

        var result = ExplorerFileSearch.Find(All(), FileNameFilter.Parse("*.md")!, maxResults: 3, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Files.Count);
        Assert.True(result.IsTruncated);
    }

    [Fact]
    public void Missing_root_returns_no_files()
    {
        var project = new ProjectDefinition { Id = "p2", Kind = ProjectKind.Folder, Name = "M", Root = _temp.Combine("missing") };
        var source = new FolderProjectSource(project, project.Root!, PhysicalFileStore.Instance);

        var result = ExplorerFileSearch.Find(source, FileNameFilter.Parse("*")!, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ProjectRootState.Missing, result.RootState);
        Assert.Empty(result.Files);
    }
}
