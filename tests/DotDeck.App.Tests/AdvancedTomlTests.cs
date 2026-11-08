using DotDeck.App.ViewModels.Editor;

namespace DotDeck.App.Tests;

public sealed class AdvancedTomlTests
{
    [Fact]
    public void Unknown_tables_are_grouped_and_aggregate_children_are_not_duplicated()
    {
        var writes = new List<string>();
        var form = new CodexConfigFormViewModel(writes.Add);
        Assert.Null(form.Load("model = 'known'\ncustom = { x = 1, y = 'two' }\narr = [1, 'two']\n[profiles.deep]\nunknown = true\n"));
        var nodes = Flatten(form.Advanced.Roots.Single()).ToList();
        Assert.DoesNotContain(nodes, n => n.Label == "model" || n.Label == "0" || n.Label == "x");
        Assert.Contains(nodes, n => n.Label == "deep");
        form.Advanced.Selected = nodes.Single(n => n.Label == "custom");
        Assert.True(form.Advanced.IsInline);
        form.Advanced.Inline.Rows.First().Value = "3";
        form.Advanced.ApplyCommand.Execute(null);
        Assert.Contains("custom = { x = 3, y = 'two' }", Assert.Single(writes), StringComparison.Ordinal);
        Assert.Contains("[profiles.deep]\nunknown = true\n", writes[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Mixed_array_edits_keep_comments_and_boolean_uses_its_control()
    {
        var writes = new List<string>();
        var form = new CodexConfigFormViewModel(writes.Add);
        Assert.Null(form.Load("# keep\narr = [\n1, # number\n'old' # text\n]\nflag = true\n"));
        form.Advanced.Selected = Flatten(form.Advanced.Roots.Single()).Single(n => n.Label == "arr");
        Assert.True(form.Advanced.IsArray);
        form.Advanced.Array.Rows[1].Value = "'new'";
        form.Advanced.ApplyCommand.Execute(null);
        Assert.Contains("# number\n", writes[0], StringComparison.Ordinal);
        Assert.Contains("'new' # text\n", writes[0], StringComparison.Ordinal);
        form.Advanced.Selected = Flatten(form.Advanced.Roots.Single()).Single(n => n.Label == "flag");
        Assert.True(form.Advanced.IsBoolean);
        form.Advanced.Boolean = false;
        form.Advanced.ApplyCommand.Execute(null);
        Assert.Contains("flag = false", writes[^1], StringComparison.Ordinal);
    }

    private static IEnumerable<AdvancedTomlNode> Flatten(AdvancedTomlNode node) => new[] { node }.Concat(node.Children.SelectMany(Flatten));
}
