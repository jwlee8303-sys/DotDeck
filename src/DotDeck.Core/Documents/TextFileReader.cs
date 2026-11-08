using System.Security.Cryptography;
using System.Text;
using DotDeck.Core.Explorer;
using DotDeck.Core.Files;

namespace DotDeck.Core.Documents;

/// <summary>The file looks binary (NUL bytes without a UTF-16/32 BOM) and is not opened as text.</summary>
public sealed class BinaryFileException(string path) : IOException($"Binary file: {path}")
{
    public string Path { get; } = path;
}

/// <summary>The file name matches the secret file policy; DotDeck never reads its content.</summary>
public sealed class SecretFileException(string path) : IOException($"Secret file is not opened: {path}")
{
    public string Path { get; } = path;
}

/// <summary>
/// Reads text files for editing. Detection order: BOM → strict UTF-8 → strict CP949 → CP949 with replacement
/// (flagged as <see cref="TextFileSnapshot.HasDecodingErrors"/>, which blocks saving).
/// </summary>
public sealed class TextFileReader(IFileStore store)
{
    /// <summary>Bytes inspected for NUL when sniffing for binary content.</summary>
    public const int SniffLength = 8 * 1024;

    /// <summary>Files larger than this need confirmation before opening.</summary>
    public const long LargeFileThreshold = 1024 * 1024;

    private const int MaxReadAttempts = 3;

    private readonly IFileStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public static bool IsLarge(long length) => length > LargeFileThreshold;

    /// <summary>Size and binary check without reading the whole file.</summary>
    public TextFileProbe Probe(string path)
    {
        EnsureNotSecret(path);
        var entry = GetFileEntry(path);
        var head = _store.ReadHead(path, SniffLength);
        return new TextFileProbe(path, entry.Length, entry.LastWriteTimeUtc, LooksBinary(head), IsLarge(entry.Length));
    }

    /// <summary>
    /// Reads and decodes the whole file. Throws <see cref="BinaryFileException"/>, <see cref="SecretFileException"/>,
    /// <see cref="FileNotFoundException"/> or the usual IO exceptions.
    /// </summary>
    public TextFileSnapshot Read(string path)
    {
        EnsureNotSecret(path);

        // Re-read if the file changed while reading so hash, mtime and content describe the same version.
        for (var attempt = 1; ; attempt++)
        {
            var before = GetFileEntry(path);
            var bytes = _store.ReadAllBytes(path);
            var after = GetFileEntry(path);
            if (before.LastWriteTimeUtc == after.LastWriteTimeUtc && before.Length == bytes.Length && after.Length == bytes.Length)
            {
                return Decode(path, bytes, after.LastWriteTimeUtc);
            }

            if (attempt == MaxReadAttempts)
            {
                throw new IOException($"File kept changing while being read: {path}");
            }
        }
    }

    /// <summary>Decodes bytes as if read from <paramref name="path"/>. Exposed for tests and for re-decoding.</summary>
    public static TextFileSnapshot Decode(string path, byte[] bytes, DateTime lastWriteTimeUtc)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var bom = TextEncodings.DetectBom(bytes);
        if (LooksBinary(bytes.AsSpan(0, Math.Min(bytes.Length, SniffLength))))
        {
            throw new BinaryFileException(path);
        }

        string text;
        TextEncodingKind kind;
        var hasErrors = false;
        if (bom is { } detected)
        {
            kind = detected;
            var body = bytes.AsSpan(TextEncodings.Preamble(kind).Length);
            if (!TryDecode(body, kind, out text))
            {
                throw new BinaryFileException(path);
            }
        }
        else if (TryDecode(bytes, TextEncodingKind.Utf8, out text))
        {
            kind = TextEncodingKind.Utf8;
        }
        else
        {
            kind = TextEncodingKind.Cp949;
            if (!TryDecode(bytes, kind, out text))
            {
                text = TextEncodings.GetLenient(kind).GetString(bytes);
                hasErrors = true;
            }
        }

        return new TextFileSnapshot(
            path,
            text,
            kind,
            LineEndingInfo.Detect(text),
            bytes.Length,
            lastWriteTimeUtc,
            ComputeHash(bytes),
            IsLarge(bytes.Length),
            hasErrors,
            RoundTrips: !hasErrors && EncodesTo(text, kind, bytes));
    }

    /// <summary>
    /// Snapshot for text DotDeck itself just encoded and wrote (no detection, so text with NUL is still accepted).
    /// </summary>
    public static TextFileSnapshot ForWrittenText(string path, string text, TextEncodingKind kind, byte[] bytes, DateTime lastWriteTimeUtc) =>
        new(path, text, kind, LineEndingInfo.Detect(text), bytes.Length, lastWriteTimeUtc, ComputeHash(bytes), IsLarge(bytes.Length),
            HasDecodingErrors: false, RoundTrips: true);

    public static string ComputeHash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>NUL byte in the sniffed region means binary (UTF-16/32 files must carry a BOM to be read as text).</summary>
    public static bool LooksBinary(ReadOnlySpan<byte> head) =>
        TextEncodings.DetectBom(head) is not (TextEncodingKind.Utf16LeBom or TextEncodingKind.Utf16BeBom or TextEncodingKind.Utf32LeBom)
        && head.Contains((byte)0);

    private static bool TryDecode(ReadOnlySpan<byte> bytes, TextEncodingKind kind, out string text)
    {
        try
        {
            text = TextEncodings.GetStrict(kind).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static bool EncodesTo(string text, TextEncodingKind kind, byte[] expected)
    {
        try
        {
            return TextEncodings.Encode(text, kind).AsSpan().SequenceEqual(expected);
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    // A link named "notes.txt" pointing at ".env" must not bypass the policy, so the final target is checked too.
    private void EnsureNotSecret(string path) => ArgumentException.ThrowIfNullOrEmpty(path);

    private FileSystemEntry GetFileEntry(string path)
    {
        var entry = _store.GetEntry(path);
        if (entry is null || entry.IsDirectory)
        {
            throw new FileNotFoundException("File not found.", path);
        }

        return entry;
    }
}
