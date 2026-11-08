using DotDeck.Core.Editors;

namespace DotDeck.Core.Tests.Editors;

public sealed class FrontmatterDocumentTests
{
    private const string Agent = "---\nname: reviewer\ndescription: |\n  Reviews code.\n  Two lines.\ntools: Read, Grep\n# keep me\ncolor: blue\nextra:\n  - a\n  - b\n---\n\nBody **text**\n";

    [Fact]
    public void Reads_scalars_blocks_and_lists()
    {
        var doc = FrontmatterDocument.Parse(Agent);

        Assert.True(doc.HasFrontmatter);
        Assert.Equal("reviewer", doc.Get("name"));
        Assert.Equal("Reviews code.\nTwo lines.", doc.Get("description"));
        Assert.Equal(["Read", "Grep"], doc.GetList("tools"));
        Assert.Equal(["a", "b"], doc.GetList("extra"));
        Assert.Null(doc.Get("model"));
        Assert.Equal("\nBody **text**\n", doc.Body);
        Assert.Equal(2, doc.LineOf("name"));
    }

    [Fact]
    public void Unchanged_document_round_trips_exactly()
    {
        Assert.Equal(Agent, FrontmatterDocument.Parse(Agent).ToString());
        var crlf = Agent.Replace("\n", "\r\n", StringComparison.Ordinal);
        Assert.Equal(crlf, FrontmatterDocument.Parse(crlf).ToString());
    }

    [Fact]
    public void Setting_known_keys_keeps_unknown_entries()
    {
        var doc = FrontmatterDocument.Parse(Agent);

        doc.Set("name", "code-reviewer");
        doc.Set("description", "One line: with colon");
        doc.SetList("tools", ["Read", "Edit"]);
        doc.Set("model", "sonnet");
        doc.Body = "\nNew body\n";

        var text = doc.ToString();
        Assert.Contains("name: code-reviewer\n", text, StringComparison.Ordinal);
        Assert.Contains("description: \"One line: with colon\"\n", text, StringComparison.Ordinal);
        Assert.Contains("tools: Read, Edit\n", text, StringComparison.Ordinal);
        Assert.Contains("# keep me\ncolor: blue\nextra:\n  - a\n  - b\nmodel: sonnet\n---\n\nNew body\n", text, StringComparison.Ordinal);

        var again = FrontmatterDocument.Parse(text);
        Assert.Equal("One line: with colon", again.Get("description"));
        Assert.Equal("blue", again.Get("color"));
    }

    [Fact]
    public void Multi_line_values_become_blocks_and_null_removes()
    {
        var doc = FrontmatterDocument.Parse(Agent);
        doc.Set("description", "a\n\nb");
        doc.Set("color", null);

        var again = FrontmatterDocument.Parse(doc.ToString());
        Assert.Equal("a\n\nb", again.Get("description"));
        Assert.False(again.Contains("color"));
    }

    [Fact]
    public void Text_without_frontmatter_gains_one_when_set()
    {
        var doc = FrontmatterDocument.Parse("# Title\n");
        Assert.False(doc.HasFrontmatter);
        Assert.Equal("# Title\n", doc.ToString());

        doc.Set("name", "x");
        Assert.Equal("---\nname: x\n---\n# Title\n", doc.ToString());
    }

    [Fact]
    public void Unclosed_frontmatter_is_reported()
    {
        var doc = FrontmatterDocument.Parse("---\nname: x\nbody");

        Assert.False(doc.HasFrontmatter);
        Assert.Contains(doc.ParseIssues, i => i.Severity == IssueSeverity.Error);
    }

    [Theory]
    [InlineData("name: 'it''s'", "it's")]
    [InlineData("name: \"a \\\"b\\\"\"", "a \"b\"")]
    [InlineData("name: plain # comment", "plain")]
    [InlineData("name: >\n  folded\n  text", "folded text")]
    [InlineData("name: |\n  kept\n# top-level comment\n", "kept")]
    [InlineData("name: [a, \"b\"]", "[a, \"b\"]")]
    public void Scalar_forms(string entry, string expected)
    {
        Assert.Equal(expected, FrontmatterDocument.Parse($"---\n{entry}\n---\n").Get("name"));
    }

    [Fact]
    public void Values_that_need_quotes_round_trip()
    {
        foreach (var value in new[] { "true", "- dash", "a: b", "x #y", "[x]", "", " pad", "it's \"q\" \\ path" })
        {
            var doc = FrontmatterDocument.Parse("---\n---\n");
            doc.Set("v", value);
            Assert.Equal(value, FrontmatterDocument.Parse(doc.ToString()).Get("v"));
        }
    }
}
