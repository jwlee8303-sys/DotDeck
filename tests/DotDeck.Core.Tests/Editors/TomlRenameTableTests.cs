using System.Text;
using DotDeck.Core.Toml;

namespace DotDeck.Core.Tests.Editors;

public sealed class TomlRenameTableTests
{
    private static TomlEditDocument Doc(string text) => TomlEditDocument.Parse(Encoding.UTF8.GetBytes(text));

    private static string Text(TomlEditDocument doc) => Encoding.UTF8.GetString(doc.ToBytes());

    [Fact]
    public void Rename_keeps_comments_and_moves_sub_tables_and_dotted_keys()
    {
        var doc = Doc("# top\n[profiles.a] # header\nmodel = 'x'\nsub.key = 1\n\n[profiles.a.extra]\nv = true\n[other]\nq = 1\n");
        doc.RenameTable(["profiles", "a"], "b");
        Assert.Equal("# top\n[profiles.b] # header\nmodel = 'x'\nsub.key = 1\n\n[profiles.b.extra]\nv = true\n[other]\nq = 1\n", Text(doc));
    }

    [Fact]
    public void Rename_parent_of_array_table_writes_key_segments_only()
    {
        var doc = Doc("[profiles.a]\nm = 1\n[[profiles.a.workers]]\nn = 'one'\n[[profiles.a.workers]]\nn = 'two'\n[profiles.a.workers.sub]\nk = 3\n");
        doc.RenameTable(["profiles", "a"], "b");
        Assert.Equal("[profiles.b]\nm = 1\n[[profiles.b.workers]]\nn = 'one'\n[[profiles.b.workers]]\nn = 'two'\n[profiles.b.workers.sub]\nk = 3\n", Text(doc));
        Assert.Contains(doc.Entries, e => e.Path.SequenceEqual(["profiles", "b", "workers", "1", "n"]) && e.Literal == "'two'");
    }

    [Fact]
    public void Rename_array_table_itself_renames_every_element()
    {
        var doc = Doc("[[a.workers]]\nn = 1\n[[a.workers]]\nn = 2\n[a.keep]\nx = 1\n");
        doc.RenameTable(["a", "workers"], "jobs");
        Assert.Equal("[[a.jobs]]\nn = 1\n[[a.jobs]]\nn = 2\n[a.keep]\nx = 1\n", Text(doc));
    }

    [Fact]
    public void Array_element_index_cannot_be_renamed()
    {
        var text = "[[a.workers]]\nn = 1\n";
        var doc = Doc(text);
        Assert.Throws<TomlEditException>(() => doc.RenameTable(["a", "workers", "0"], "x"));
        Assert.Equal(text, Text(doc));
    }

    [Theory]
    [InlineData("[profiles.a]\nm = 1\n[profiles.b.child]\nc = 2\n")]            // implicit table b
    [InlineData("[profiles.a]\nm = 1\n[profiles.b]\nc = 2\n")]                  // explicit table b
    [InlineData("[profiles]\nb = { x = 1 }\n[profiles.a]\nm = 1\n")]           // inline table b
    [InlineData("[profiles]\nb.x = 1\n[profiles.a]\nm = 1\n")]                 // dotted key b
    [InlineData("[profiles]\nb = 'value'\n[profiles.a]\nm = 1\n")]             // plain value b
    [InlineData("[profiles.a]\nm = 1\n[[profiles.b]]\nc = 2\n")]                // array table b
    public void Rename_to_an_occupied_name_is_rejected_without_merging(string text)
    {
        var doc = Doc(text);
        var error = Assert.Throws<TomlEditException>(() => doc.RenameTable(["profiles", "a"], "b"));
        Assert.Contains("이미", error.Message, StringComparison.Ordinal);
        Assert.Equal(text, Text(doc));
    }

    [Fact]
    public void Inline_table_rename_collides_with_existing_table()
    {
        var text = "[srv]\na = { x = 1 }\n[srv.b]\ny = 2\n";
        var doc = Doc(text);
        Assert.Throws<TomlEditException>(() => doc.RenameTable(["srv", "a"], "b"));
        Assert.Equal(text, Text(doc));
    }

    [Fact]
    public void Inline_table_value_can_be_renamed_when_free()
    {
        var doc = Doc("[srv]\na = { x = 1 } # c\n");
        doc.RenameTable(["srv", "a"], "z");
        Assert.Equal("[srv]\nz = { x = 1 } # c\n", Text(doc));
    }

    [Fact]
    public void Same_name_is_a_no_op()
    {
        var text = "[profiles.a]\nm = 1\n";
        var doc = Doc(text);
        doc.RenameTable(["profiles", "a"], "a");
        Assert.Equal(text, Text(doc));
    }

    [Fact]
    public void String_list_edit_refuses_mixed_arrays_and_keeps_them()
    {
        var text = "args = ['first', 1, true] # mixed\n";
        var doc = Doc(text);
        Assert.Throws<TomlEditException>(() => doc.SetStringList(["args"], ["changed", "1", "true"]));
        Assert.Equal(text, Text(doc));
    }

    [Fact]
    public void Array_literal_edit_keeps_unchanged_element_literals()
    {
        var doc = Doc("args = [ 'first' , 0x10, true, \"q\" ]\n");
        doc.SetArrayLiterals(["args"], ["'changed'", "0x10", "true", "\"q\""]);
        Assert.Equal("args = [ 'changed' , 0x10, true, \"q\" ]\n", Text(doc));
    }
}
