using System.Text;
using DotDeck.Core.Documents;
using DotDeck.Core.Files;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.Documents;

public sealed class TextFileReaderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly TextFileReader _reader = new(PhysicalFileStore.Instance);

    public void Dispose() => _temp.Dispose();

    private string Write(string name, byte[] bytes)
    {
        var path = _temp.Combine(name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Cp949(string text)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(949).GetBytes(text);
    }

    [Fact]
    public void Utf8_without_bom_is_detected_and_round_trips()
    {
        var bytes = new UTF8Encoding(false).GetBytes("안녕\nworld\n");
        var snapshot = _reader.Read(Write("a.md", bytes));

        Assert.Equal(TextEncodingKind.Utf8, snapshot.Encoding);
        Assert.Equal("안녕\nworld\n", snapshot.Text);
        Assert.Equal(LineEnding.Lf, snapshot.LineEndings.Dominant);
        Assert.False(snapshot.LineEndings.IsMixed);
        Assert.True(snapshot.RoundTrips);
        Assert.False(snapshot.HasDecodingErrors);
        Assert.Equal(bytes.Length, snapshot.Length);
        Assert.Equal(TextFileReader.ComputeHash(bytes), snapshot.Sha256);
    }

    [Fact]
    public void Utf8_bom_is_stripped_from_text_and_preserved_in_kind()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("x\r\ny")];
        var snapshot = _reader.Read(Write("a.txt", bytes));

        Assert.Equal(TextEncodingKind.Utf8Bom, snapshot.Encoding);
        Assert.Equal("x\r\ny", snapshot.Text);
        Assert.Equal(LineEnding.CrLf, snapshot.LineEndings.Dominant);
        Assert.True(snapshot.RoundTrips);
        Assert.Equal(bytes, TextEncodings.Encode(snapshot.Text, snapshot.Encoding));
    }

    [Theory]
    [InlineData(false, TextEncodingKind.Utf16LeBom)]
    [InlineData(true, TextEncodingKind.Utf16BeBom)]
    public void Utf16_with_bom_is_text_despite_nul_bytes(bool bigEndian, TextEncodingKind expected)
    {
        var encoding = new UnicodeEncoding(bigEndian, true);
        byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes("ab\r\n한")];
        var snapshot = _reader.Read(Write("u.txt", bytes));

        Assert.Equal(expected, snapshot.Encoding);
        Assert.Equal("ab\r\n한", snapshot.Text);
        Assert.True(snapshot.RoundTrips);
    }

    [Fact]
    public void Utf32_le_bom_is_not_mistaken_for_utf16()
    {
        var encoding = new UTF32Encoding(false, true);
        byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes("z")];
        var snapshot = _reader.Read(Write("u32.txt", bytes));

        Assert.Equal(TextEncodingKind.Utf32LeBom, snapshot.Encoding);
        Assert.Equal("z", snapshot.Text);
    }

    [Fact]
    public void Cp949_is_used_when_strict_utf8_fails()
    {
        var bytes = Cp949("한글 설정\r\n");
        var snapshot = _reader.Read(Write("k.ini", bytes));

        Assert.Equal(TextEncodingKind.Cp949, snapshot.Encoding);
        Assert.Equal("한글 설정\r\n", snapshot.Text);
        Assert.True(snapshot.RoundTrips);
        Assert.False(snapshot.HasDecodingErrors);
    }

    [Fact]
    public void Undecodable_bytes_are_flagged_and_do_not_round_trip()
    {
        // A CP949 lead byte (0xB0) cut off at end of file is invalid in both UTF-8 and CP949.
        byte[] bytes = [(byte)'a', (byte)'b', 0xB0];
        var snapshot = _reader.Read(Write("bad.txt", bytes));

        Assert.Equal(TextEncodingKind.Cp949, snapshot.Encoding);
        Assert.True(snapshot.HasDecodingErrors);
        Assert.False(snapshot.RoundTrips);
    }

    [Fact]
    public void Nul_byte_without_bom_is_binary()
    {
        var path = Write("b.bin", [(byte)'a', 0, (byte)'b']);

        Assert.True(_reader.Probe(path).IsBinary);
        Assert.Throws<BinaryFileException>(() => _reader.Read(path));
    }

    [Fact]
    public void Nul_byte_after_utf8_bom_is_binary_in_probe_and_read()
    {
        var path = Write("bom-nul.bin", [0xEF, 0xBB, 0xBF, 0x00]);

        Assert.True(_reader.Probe(path).IsBinary);
        Assert.Throws<BinaryFileException>(() => _reader.Read(path));
    }

    [Fact]
    public void File_that_keeps_changing_during_read_is_rejected()
    {
        var path = Write("moving.txt", "a"u8.ToArray());
        var reader = new TextFileReader(new ChangingStore());

        Assert.Throws<IOException>(() => reader.Read(path));
    }

    private sealed class ChangingStore : IFileStore
    {
        private int _calls;
        private readonly IFileStore _inner = PhysicalFileStore.Instance;

        public FileSystemEntry? GetEntry(string path) =>
            _inner.GetEntry(path) is { } e ? e with { LastWriteTimeUtc = e.LastWriteTimeUtc.AddSeconds(++_calls) } : null;

        public bool FileExists(string path) => _inner.FileExists(path);
        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
        public IReadOnlyList<FileSystemEntry> GetChildren(string directory) => _inner.GetChildren(directory);
        public byte[] ReadAllBytes(string path) => _inner.ReadAllBytes(path);
        public byte[] ReadHead(string path, int maxBytes) => _inner.ReadHead(path, maxBytes);
        public string? ResolveLinkTarget(string path) => _inner.ResolveLinkTarget(path);
        public void ReplaceFile(string source, string destination) => _inner.ReplaceFile(source, destination);
        public void DeleteDirectory(string path) => _inner.DeleteDirectory(path);
        public void WriteAllBytes(string path, byte[] bytes) => _inner.WriteAllBytes(path, bytes);
        public void CreateDirectory(string path) => _inner.CreateDirectory(path);
        public void CreateFile(string path) => _inner.CreateFile(path);
        public void Move(string source, string destination, bool overwrite = false) => _inner.Move(source, destination, overwrite);
        public void Copy(string source, string destination) => _inner.Copy(source, destination);
        public void DeleteFile(string path) => _inner.DeleteFile(path);
        public IDisposable AcquireLock(string lockPath, TimeSpan timeout) => _inner.AcquireLock(lockPath, timeout);
    }

    [Fact]
    public void Nul_byte_beyond_sniff_window_is_not_binary()
    {
        var bytes = new byte[TextFileReader.SniffLength + 10];
        bytes.AsSpan().Fill((byte)'a');
        bytes[^1] = 0;
        var path = Write("late.txt", bytes);

        Assert.False(_reader.Probe(path).IsBinary);
        Assert.Equal(TextEncodingKind.Utf8, _reader.Read(path).Encoding);
    }

    [Fact]
    public void Mixed_line_endings_are_reported_with_majority_dominant()
    {
        var info = LineEndingInfo.Detect("a\nb\nc\r\nd\re");

        Assert.True(info.IsMixed);
        Assert.Equal(LineEnding.Lf, info.Dominant);
        Assert.Equal((1, 2, 1), (info.CrLfCount, info.LfCount, info.CrCount));
    }

    [Fact]
    public void Text_without_line_breaks_defaults_to_crlf()
    {
        var info = LineEndingInfo.Detect("single");

        Assert.False(info.HasAny);
        Assert.Equal(LineEnding.CrLf, info.Dominant);
    }

    [Fact]
    public void Large_flag_uses_one_mebibyte_boundary()
    {
        var exact = Write("exact.txt", Enumerable.Repeat((byte)'a', (int)TextFileReader.LargeFileThreshold).ToArray());
        var over = Write("over.txt", Enumerable.Repeat((byte)'a', (int)TextFileReader.LargeFileThreshold + 1).ToArray());

        Assert.False(_reader.Probe(exact).IsLarge);
        Assert.True(_reader.Probe(over).IsLarge);
        Assert.True(_reader.Read(over).IsLarge);
    }

    [Fact]
    public void Empty_file_is_utf8_text()
    {
        var snapshot = _reader.Read(Write("empty.txt", []));

        Assert.Equal(TextEncodingKind.Utf8, snapshot.Encoding);
        Assert.Equal(string.Empty, snapshot.Text);
        Assert.True(snapshot.RoundTrips);
    }

    [Theory]
    [InlineData(".env")]
    [InlineData("id_rsa")]
    [InlineData("cert.pem")]
    public void Secret_files_are_never_read(string name)
    {
        var path = Write(name, Encoding.UTF8.GetBytes("SECRET=1"));

        Assert.Throws<SecretFileException>(() => _reader.Read(path));
        Assert.Throws<SecretFileException>(() => _reader.Probe(path));
    }

    [Fact]
    public void Link_to_secret_file_is_never_read()
    {
        Write(".env", Encoding.UTF8.GetBytes("SECRET=1"));
        var link = _temp.Combine("notes.txt");
        try
        {
            File.CreateSymbolicLink(link, _temp.Combine(".env"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Symbolic links need Developer Mode or elevation.");
        }

        Assert.Throws<SecretFileException>(() => _reader.Read(link));
        Assert.Throws<SecretFileException>(() => _reader.Probe(link));
    }

    [Fact]
    public void Missing_file_or_directory_throws_file_not_found()
    {
        Assert.Throws<FileNotFoundException>(() => _reader.Read(_temp.Combine("missing.txt")));
        Directory.CreateDirectory(_temp.Combine("dir"));
        Assert.Throws<FileNotFoundException>(() => _reader.Read(_temp.Combine("dir")));
    }

    [Fact]
    public void Lone_surrogate_text_cannot_be_encoded()
    {
        Assert.Throws<EncoderFallbackException>(() => TextEncodings.Encode("\uD800", TextEncodingKind.Utf8));
    }

    [Fact]
    public void Cp949_rejects_characters_it_cannot_represent()
    {
        Assert.Throws<EncoderFallbackException>(() => TextEncodings.Encode("😀", TextEncodingKind.Cp949));
    }
}
