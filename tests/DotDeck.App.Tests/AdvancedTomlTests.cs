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

    [Fact]
    public void Mixed_mcp_args_are_read_only_in_the_form_and_edited_losslessly_in_the_tree()
    {
        var writes = new List<string>();
        var form = new CodexConfigFormViewModel(writes.Add);
        Assert.Null(form.Load("[mcp_servers.s]\ncommand = 'run'\nargs = ['first', 1, true] # mixed\n"));
        var server = form.SelectedServer!;
        Assert.False(server.ArgsEditable);
        Assert.NotNull(server.ArgsNotice);
        server.Args.Rows[0].Value = "changed";
        Assert.Empty(writes);

        var args = Flatten(form.Advanced.Roots.Single()).Single(n => n.Label == "args");
        form.Advanced.Selected = args;
        Assert.True(form.Advanced.IsArray);
        form.Advanced.Array.Rows[0].Value = "'changed'";
        form.Advanced.ApplyCommand.Execute(null);
        Assert.Contains("args = ['changed', 1, true] # mixed\n", Assert.Single(writes), StringComparison.Ordinal);
    }

    [Fact]
    public void String_mcp_args_stay_editable_in_the_form()
    {
        var writes = new List<string>();
        var form = new CodexConfigFormViewModel(writes.Add);
        Assert.Null(form.Load("[mcp_servers.s]\nargs = ['a', \"b\"]\n"));
        Assert.True(form.SelectedServer!.ArgsEditable);
        form.SelectedServer.Args.Rows[1].Value = "c";
        Assert.Contains("args = ['a', \"c\"]", Assert.Single(writes), StringComparison.Ordinal);
    }

    [Fact]
    public void Renaming_a_profile_onto_an_existing_one_is_refused_with_a_message()
    {
        var writes = new List<string>();
        var form = new CodexConfigFormViewModel(writes.Add);
        Assert.Null(form.Load("[profiles.a]\nmodel = 'x'\n[profiles.b.child]\nc = 1\n"));
        form.SelectedProfile = "a";
        form.ProfileName = "b";
        form.RenameProfileCommand.Execute(null);
        Assert.Empty(writes);
        Assert.NotNull(form.Message);
    }

    private static IEnumerable<AdvancedTomlNode> Flatten(AdvancedTomlNode node) => new[] { node }.Concat(node.Children.SelectMany(Flatten));
}
