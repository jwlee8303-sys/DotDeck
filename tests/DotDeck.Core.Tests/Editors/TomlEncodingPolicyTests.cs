using DotDeck.Core.Documents;

namespace DotDeck.Core.Tests.Editors;

public sealed class TomlEncodingPolicyTests
{
    [Theory]
    [InlineData(TextEncodingKind.Cp949)]
    [InlineData(TextEncodingKind.Utf16LeBom)]
    [InlineData(TextEncodingKind.Utf16BeBom)]
    public void Non_utf8_toml_opens_read_only_and_save_is_blocked_even_with_encoding_override(TextEncodingKind kind)
    {
        const string text = "name = '한글'\r\n";
        var bytes = TextEncodings.Encode(text, kind);
        var snapshot = TextFileReader.Decode(@"C:\fake\config.toml", bytes, DateTime.UtcNow);
        Assert.Equal(text, snapshot.Text);
        Assert.True(snapshot.IsReadOnly);
        Assert.Contains("UTF-8", snapshot.ReadOnlyReason, StringComparison.Ordinal);
        Assert.False(SafeWriter.Prepare(snapshot.Path, snapshot, "name = 'changed'", TextEncodingKind.Utf8).CanCommit);
        Assert.Equal(bytes, TextEncodings.Encode(snapshot.Text, snapshot.Encoding));
    }

    [Theory]
    [InlineData(TextEncodingKind.Utf8)]
    [InlineData(TextEncodingKind.Utf8Bom)]
    public void Utf8_toml_is_editable(TextEncodingKind kind)
    {
        var snapshot = TextFileReader.Decode(@"C:\fake\config.toml", TextEncodings.Encode("name = '한글'", kind), DateTime.UtcNow);
        Assert.False(snapshot.IsReadOnly);
        Assert.Null(snapshot.ReadOnlyReason);
    }
}
