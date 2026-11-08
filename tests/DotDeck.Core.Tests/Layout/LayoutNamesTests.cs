using DotDeck.Core.Layout;

namespace DotDeck.Core.Tests.Layout;

public sealed class LayoutNamesTests
{
    [Theory]
    [InlineData("코딩", true)]
    [InlineData("  wide screen  ", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("a/b", false)]
    [InlineData(@"..\x", false)]
    [InlineData("CON", false)]
    [InlineData("name.", false)]
    public void Names_must_be_usable_file_names(string name, bool valid)
    {
        Assert.Equal(valid, LayoutNames.Validate(name) is null);
    }

    [Fact]
    public void Too_long_names_are_rejected()
    {
        Assert.NotNull(LayoutNames.Validate(new string('a', LayoutNames.MaxLength + 1)));
        Assert.Null(LayoutNames.Validate(new string('a', LayoutNames.MaxLength)));
    }

    [Fact]
    public void Listing_keeps_only_layout_files_sorted()
    {
        var names = LayoutNames.FromFileNames(["b.xml", "A.XML", "notes.txt", "last.xml.tmp", "CON.xml"]);

        Assert.Equal(["A", "b"], names);
        Assert.Equal("코딩.xml", LayoutNames.FileName(" 코딩 "));
    }
}
