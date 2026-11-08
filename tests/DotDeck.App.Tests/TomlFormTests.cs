using DotDeck.App.ViewModels.Editor;

namespace DotDeck.App.Tests;

public sealed class TomlFormTests
{
    [Fact]
    public void Unsupported_operations_are_disabled_and_valid_edits_apply_once()
    {
        var writes = new List<string>();
        var form = new TomlFormViewModel(writes.Add);
        Assert.Null(form.Load("# keep\ninline = { x = 1 }\narray = [1, 2]\n"));
        Assert.Empty(writes);
        form.Selected = form.Rows.Single(r => r.Label == "inline.x");
        Assert.False(form.DeleteKeyCommand.CanExecute(null));
        form.Selected = form.Rows.Single(r => r.Label == "array.0");
        Assert.False(form.DeleteKeyCommand.CanExecute(null));
        form.Path = "inline.new";
        Assert.False(form.AddKeyCommand.CanExecute(null));
        form.Path = "a.b";
        form.CreateTableCommand.Execute(null);
        Assert.Single(writes);
        form.Path = "a.b.value";
        form.Literal = "true";
        Assert.True(form.AddKeyCommand.CanExecute(null));
        form.AddKeyCommand.Execute(null);
        Assert.Equal(2, writes.Count);
        form.Selected = form.Rows.Single(r => r.Label == "a.b.value");
        Assert.True(form.DeleteKeyCommand.CanExecute(null));
        form.DeleteKeyCommand.Execute(null);
        form.CleanupCommand.Execute(null);
        Assert.Equal("# keep\ninline = { x = 1 }\narray = [1, 2]\n", writes[^1]);
    }

    [Fact]
    public void Secret_documents_are_masked_and_all_edit_commands_disabled()
    {
        var form = new TomlFormViewModel(_ => Assert.Fail("Must not write"));
        Assert.Null(form.Load("[mcp_servers.demo.env]\nTOKEN = 'fake-sensitive-value'\n"));
        Assert.True(form.CanEdit);
        Assert.All(form.Rows, r => Assert.NotNull(r.Literal));
        form.Selected = form.Rows.First();
        form.Path = "new";
        Assert.True(form.ApplyValueCommand.CanExecute(null));
        Assert.True(form.AddKeyCommand.CanExecute(null));
        Assert.True(form.DeleteKeyCommand.CanExecute(null));
        Assert.True(form.CreateTableCommand.CanExecute(null));
        Assert.True(form.CleanupCommand.CanExecute(null));
    }

    [Fact]
    public void Invalid_patch_and_secret_key_addition_never_write_to_buffer()
    {
        var form = new TomlFormViewModel(_ => Assert.Fail("Must not write"));
        Assert.Null(form.Load("x = 1\n"));
        form.Selected = form.Rows.First();
        form.Literal = "2\ninjected = true";
        form.ApplyValueCommand.Execute(null);
        Assert.Equal("1", form.Rows.First().Literal);
        form.Path = "inline.missing";
        form.Literal = "'fake'";
        form.AddKeyCommand.Execute(null);
        Assert.Single(form.Rows);
    }
}
