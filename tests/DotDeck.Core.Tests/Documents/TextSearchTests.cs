using DotDeck.Core.Documents;

namespace DotDeck.Core.Tests.Documents;

public sealed class TextSearchTests
{
    private static System.Text.RegularExpressions.Regex Build(SearchOptions options) =>
        TextSearch.TryBuild(options, out var error) ?? throw new InvalidOperationException(error);

    [Fact]
    public void Plain_search_escapes_regex_characters_and_ignores_case_by_default()
    {
        var regex = Build(new SearchOptions("a.b"));

        Assert.Equal([new TextMatch(4, 3)], TextSearch.FindAll("axb A.B", regex));
    }

    [Fact]
    public void Match_case_and_whole_word_are_respected()
    {
        var regex = Build(new SearchOptions("key", MatchCase: true, WholeWord: true));

        Assert.Equal([new TextMatch(0, 3)], TextSearch.FindAll("key keys Key monkey", regex));
    }

    [Fact]
    public void Invalid_regex_reports_error()
    {
        Assert.Null(TextSearch.TryBuild(new SearchOptions("(", UseRegex: true), out var error));
        Assert.NotNull(error);
        Assert.Null(TextSearch.TryBuild(new SearchOptions(string.Empty), out _));
    }

    [Fact]
    public void Find_next_wraps_forward_and_backward()
    {
        var regex = Build(new SearchOptions("x"));
        const string text = "x-x-x";

        Assert.Equal(new TextMatch(2, 1), TextSearch.FindNext(text, regex, 1));
        Assert.Equal(new TextMatch(0, 1), TextSearch.FindNext(text, regex, 5));
        Assert.Equal(new TextMatch(2, 1), TextSearch.FindNext(text, regex, 4, backward: true));
        Assert.Equal(new TextMatch(4, 1), TextSearch.FindNext(text, regex, 0, backward: true));
        Assert.Null(TextSearch.FindNext("none", regex, 0));
    }

    [Fact]
    public void Empty_regex_matches_are_skipped()
    {
        var regex = Build(new SearchOptions("a*", UseRegex: true));

        Assert.Equal([new TextMatch(1, 2)], TextSearch.FindAll("baab", regex));
        Assert.Equal(("bXb", 1), TextSearch.ReplaceAll("baab", regex, "X", useRegex: true));
    }

    [Fact]
    public void Replace_all_expands_groups_only_in_regex_mode()
    {
        var regex = Build(new SearchOptions(@"(\w+)=(\d)", UseRegex: true));
        Assert.Equal(("1=a, 2=b", 2), TextSearch.ReplaceAll("a=1, b=2", regex, "$2=$1", useRegex: true));

        var plain = Build(new SearchOptions("a"));
        Assert.Equal(("$1b$1", 2), TextSearch.ReplaceAll("aba", plain, "$1", useRegex: false));
    }

    [Fact]
    public void Expand_replacement_rejects_stale_match()
    {
        var regex = Build(new SearchOptions(@"v(\d)", UseRegex: true));

        Assert.Equal("w1", TextSearch.ExpandReplacement("v1", regex, new TextMatch(0, 2), "w$1", useRegex: true));
        Assert.Null(TextSearch.ExpandReplacement("xx", regex, new TextMatch(0, 2), "w$1", useRegex: true));
    }
}
