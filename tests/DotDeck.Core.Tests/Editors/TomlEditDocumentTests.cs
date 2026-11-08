using System.Text;
using DotDeck.Core.Toml;

namespace DotDeck.Core.Tests.Editors;

public sealed class TomlEditDocumentTests
{
    [Theory]
    [InlineData(false, "\n")]
    [InlineData(true, "\r\n")]
    [InlineData(true, "\n")]
    [InlineData(false, "\r\n")]
    public void No_edit_round_trip_is_byte_identical(bool bom, string newline)
    {
        var text = "# 한국어\nmodel = \"example\" # keep\n[projects.'d:\\project\\dotdeck']\ntrust_level = 'trusted'\n[features]\na.b = true\ninline = { x = 1, y = 'two' }\n[[agents]]\nname = 'first'\n[[agents]]\nname = 'second'\n".Replace("\n", newline, StringComparison.Ordinal);
        var bytes = Encode(text, bom);
        Assert.Equal(bytes, TomlEditDocument.Parse(bytes).ToBytes());
    }

    [Fact]
    public void Value_patch_preserves_every_other_byte_and_literal_quotes()
    {
        const string text = "# comment\r\n[projects.'d:\\project\\dotdeck']\r\ntrust_level  = 'untrusted' # keep\r\n";
        var document = TomlEditDocument.Parse(Encode(text, true));
        document.SetString(["projects", @"d:\project\dotdeck", "trust_level"], "trusted");
        Assert.Equal(Encode(text.Replace("'untrusted'", "'trusted'", StringComparison.Ordinal), true), document.ToBytes());
    }

    [Fact]
    public void Dotted_inline_and_array_table_values_are_addressable()
    {
        var doc = TomlEditDocument.Parse(Encode("a.b = true\ninline = { x = 1, y = 'two' }\n[[agents]]\nname = 'first'\n[[agents]]\nname = 'second'\n", false));
        doc.SetValue(["a", "b"], "false");
        doc.SetValue(["inline", "x"], "2");
        doc.SetString(["agents", "1", "name"], "changed");
        Assert.Equal("a.b = false\ninline = { x = 2, y = 'two' }\n[[agents]]\nname = 'first'\n[[agents]]\nname = 'changed'\n", Encoding.UTF8.GetString(doc.ToBytes()));
    }

    [Fact]
    public void Root_insertion_precedes_tables_and_table_insertion_stays_in_its_section()
    {
        var doc = TomlEditDocument.Parse(Encode("# keep\r\n[first]\r\na = 1\r\n[second]\r\nb = 2", false));
        doc.SetString(["path"], @"C:\new\folder");
        doc.SetValue(["first", "enabled"], "true");
        var text = Encoding.UTF8.GetString(doc.ToBytes());
        Assert.Equal("# keep\r\npath = 'C:\\new\\folder'\r\n[first]\r\na = 1\r\nenabled = true\r\n[second]\r\nb = 2", text);
    }

    [Theory]
    [InlineData("a = [")]
    [InlineData("a = 1\na = 2")]
    public void Parse_errors_have_line_and_column(string text)
    {
        var error = Assert.Throws<TomlEditException>(() => TomlEditDocument.Parse(Encode(text, false)));
        Assert.True(error.Line > 0);
        Assert.True(error.Column > 0);
    }

    [Fact]
    public void Invalid_or_injecting_patch_does_not_change_original()
    {
        var original = Encode("a = 1\n", true);
        var doc = TomlEditDocument.Parse(original);
        Assert.Throws<TomlEditException>(() => doc.SetValue(["a"], "2\nevil = true"));
        Assert.Equal(original, doc.ToBytes());
    }

    [Theory]
    [InlineData("line one\nline two")]
    [InlineData("quote ' and \" and \\")]
    [InlineData("한글 😀")]
    public void Strings_are_decoded_back_to_the_requested_value(string value)
    {
        var doc = TomlEditDocument.Parse(Encode("name = 'old' # keep\n", false));
        doc.SetString(["name"], value);
        var syntax = Tomlyn.Parsing.SyntaxParser.Parse(Encoding.UTF8.GetString(doc.ToBytes()));
        Assert.Equal(value, Assert.IsType<Tomlyn.Syntax.StringValueSyntax>(syntax.KeyValues.First().Value).Value);
    }

    [Fact]
    public void Nested_array_tables_and_arrays_patch_only_the_selected_element()
    {
        const string input = "[[items]]\nname='a'\n[items.options]\nvalues=[1, 2]\n[[items.children]]\nname='child'\n[[items]]\nname='b'\n";
        var doc = TomlEditDocument.Parse(Encode(input, false));
        doc.SetValue(["items", "0", "options", "values", "1"], "3");
        doc.SetString(["items", "0", "children", "0", "name"], "updated");
        Assert.Equal(input.Replace("[1, 2]", "[1, 3]", StringComparison.Ordinal).Replace("'child'", "'updated'", StringComparison.Ordinal), Encoding.UTF8.GetString(doc.ToBytes()));
    }

    [Fact]
    public void Equal_string_edit_preserves_escapes_and_multiline_styles_survive_edits()
    {
        var bytes = Encode("a = \"\\u0061\"\nb = '''first\nsecond'''\nc = \"\"\"old\"\"\"\n", true);
        var doc = TomlEditDocument.Parse(bytes);
        doc.SetString(["a"], "a");
        Assert.Equal(bytes, doc.ToBytes());
        doc.SetString(["b"], "next\nvalue");
        doc.SetString(["c"], "new");
        var text = Encoding.UTF8.GetString(doc.ToBytes());
        Assert.Contains("b = '''next\nvalue'''", text, StringComparison.Ordinal);
        Assert.Contains("c = \"\"\"new\"\"\"", text, StringComparison.Ordinal);
    }

    private static byte[] Encode(string text, bool bom) => [.. bom ? Encoding.UTF8.GetPreamble() : [], .. Encoding.UTF8.GetBytes(text)];
}
