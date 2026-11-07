using DotDeck.Core.Explorer;
using DotDeck.Core.Files;
using DotDeck.Core.Projects;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.Explorer;

public sealed class VisibleItemsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Policy_shows_checked_items_their_content_and_their_ancestors()
    {
        var policy = new VisibleItemsPolicy(["agents", @"skills\a\SKILL.md", "CLAUDE.md"]);

        Assert.True(policy.IsVisible("CLAUDE.md", false));
        Assert.True(policy.IsVisible(@"agents\x.md", false));
        Assert.True(policy.IsVisible("skills", true));
        Assert.True(policy.IsVisible(@"skills\a", true));
        Assert.False(policy.IsVisible(@"skills\b", true));
        Assert.False(policy.IsVisible("settings.json", false));
        Assert.True(new VisibleItemsPolicy(null).IsVisible("anything", false));
    }

    [Fact]
    public void Folder_source_filters_unless_edit_mode_shows_all()
    {
        Directory.CreateDirectory(_temp.Combine("keep"));
        Directory.CreateDirectory(_temp.Combine("drop"));
        File.WriteAllText(_temp.Combine("a.md"), "a");
        File.WriteAllText(_temp.Combine("b.md"), "b");
        var project = new ProjectDefinition { Id = "p", Kind = ProjectKind.Folder, Name = "P", Root = _temp.Path, VisibleItems = ["keep", "a.md"] };
        var source = new FolderProjectSource(project, _temp.Path, PhysicalFileStore.Instance);

        Assert.Equal(["keep", "a.md"], source.GetChildren(null).Select(n => n.Name));

        source.ShowAllItems = true;
        Assert.Equal(["drop", "keep", "a.md", "b.md"], source.GetChildren(null).Select(n => n.Name));
    }

    [Fact]
    public void Visible_items_round_trip_through_projects_json()
    {
        var path = _temp.Combine("projects.json");
        var registry = new ProjectRegistry(PhysicalFileStore.Instance, path, TimeProvider.System, new ListAppLog());
        registry.Load();
        var project = registry.AddFolder("P", _temp.Path);
        registry.SetVisibleItems(project.Id, ["b/c.md", "a", "a", " "]);

        var again = new ProjectRegistry(PhysicalFileStore.Instance, path, TimeProvider.System, new ListAppLog());
        again.Load();

        Assert.Equal(["a", Path.Combine("b", "c.md")], again.Find(project.Id)!.VisibleItems);
    }
}
