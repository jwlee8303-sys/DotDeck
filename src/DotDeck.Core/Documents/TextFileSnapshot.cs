using System.Text;

namespace DotDeck.Core.Documents;

/// <summary>Text encodings DotDeck can detect and write back unchanged.</summary>
public enum TextEncodingKind
{
    Utf8,
    Utf8Bom,
    Utf16LeBom,
    Utf16BeBom,
    Utf32LeBom,
    Cp949,
}

/// <summary>Line ending style.</summary>
public enum LineEnding
{
    CrLf,
    Lf,
    Cr,
}

/// <summary>Detected line endings. <see cref="Dominant"/> is used for new lines; <see cref="IsMixed"/> warns the user.</summary>
public sealed record LineEndingInfo(LineEnding Dominant, bool IsMixed, int CrLfCount, int LfCount, int CrCount)
{
    public bool HasAny => CrLfCount + LfCount + CrCount > 0;

    /// <summary>Counts line endings in <paramref name="text"/>. Text without line breaks defaults to CRLF.</summary>
    public static LineEndingInfo Detect(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int crlf = 0, lf = 0, cr = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '\r' when i + 1 < text.Length && text[i + 1] == '\n':
                    crlf++;
                    i++;
                    break;
                case '\r':
                    cr++;
                    break;
                case '\n':
                    lf++;
                    break;
            }
        }

        var kinds = (crlf > 0 ? 1 : 0) + (lf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
        var dominant = lf > crlf && lf >= cr ? LineEnding.Lf
            : cr > crlf && cr > lf ? LineEnding.Cr
            : LineEnding.CrLf;
        return new LineEndingInfo(dominant, kinds > 1, crlf, lf, cr);
    }
}

/// <summary>Cheap pre-open check: size and binary sniffing from the first bytes only.</summary>
public sealed record TextFileProbe(string Path, long Length, DateTime LastWriteTimeUtc, bool IsBinary, bool IsLarge);

/// <summary>
/// A text file decoded from disk, with everything needed to write it back byte-for-byte:
/// encoding (incl. BOM), line endings, and the hash/mtime used to detect external changes.
/// </summary>
public sealed record TextFileSnapshot(
    string Path,
    string Text,
    TextEncodingKind Encoding,
    LineEndingInfo LineEndings,
    long Length,
    DateTime LastWriteTimeUtc,
    string Sha256,
    bool IsLarge,
    bool HasDecodingErrors,
    bool RoundTrips)
{
    /// <summary>Display name for the status bar.</summary>
    public string EncodingDisplayName => TextEncodings.DisplayName(Encoding);
}

/// <summary>Encoding helpers shared by the reader and the writer.</summary>
public static class TextEncodings
{
    static TextEncodings() => System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static readonly byte[] s_utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] s_utf16LeBom = [0xFF, 0xFE];
    private static readonly byte[] s_utf16BeBom = [0xFE, 0xFF];
    private static readonly byte[] s_utf32LeBom = [0xFF, 0xFE, 0x00, 0x00];

    /// <summary>Strict encoding (throws on invalid input/unencodable characters), without preamble.</summary>
    public static Encoding GetStrict(TextEncodingKind kind) => kind switch
    {
        TextEncodingKind.Utf8 or TextEncodingKind.Utf8Bom => new UTF8Encoding(false, true),
        TextEncodingKind.Utf16LeBom => new UnicodeEncoding(false, false, true),
        TextEncodingKind.Utf16BeBom => new UnicodeEncoding(true, false, true),
        TextEncodingKind.Utf32LeBom => new UTF32Encoding(false, false, true),
        TextEncodingKind.Cp949 => Encoding.GetEncoding(949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Replacement-fallback variant used only when strict decoding failed.</summary>
    internal static Encoding GetLenient(TextEncodingKind kind) => kind == TextEncodingKind.Cp949
        ? Encoding.GetEncoding(949, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback)
        : throw new ArgumentOutOfRangeException(nameof(kind));

    public static ReadOnlySpan<byte> Preamble(TextEncodingKind kind) => kind switch
    {
        TextEncodingKind.Utf8Bom => s_utf8Bom,
        TextEncodingKind.Utf16LeBom => s_utf16LeBom,
        TextEncodingKind.Utf16BeBom => s_utf16BeBom,
        TextEncodingKind.Utf32LeBom => s_utf32LeBom,
        _ => [],
    };

    /// <summary>Encodes text with the preamble. Throws <see cref="EncoderFallbackException"/> for unrepresentable characters.</summary>
    public static byte[] Encode(string text, TextEncodingKind kind)
    {
        var preamble = Preamble(kind);
        var body = GetStrict(kind).GetBytes(text);
        var result = new byte[preamble.Length + body.Length];
        preamble.CopyTo(result);
        body.CopyTo(result, preamble.Length);
        return result;
    }

    /// <summary>Detects a BOM. Returns null when there is none.</summary>
    public static TextEncodingKind? DetectBom(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(s_utf32LeBom))
        {
            return TextEncodingKind.Utf32LeBom;
        }

        if (bytes.StartsWith(s_utf8Bom))
        {
            return TextEncodingKind.Utf8Bom;
        }

        if (bytes.StartsWith(s_utf16LeBom))
        {
            return TextEncodingKind.Utf16LeBom;
        }

        return bytes.StartsWith(s_utf16BeBom) ? TextEncodingKind.Utf16BeBom : null;
    }

    public static string DisplayName(TextEncodingKind kind) => kind switch
    {
        TextEncodingKind.Utf8 => "UTF-8",
        TextEncodingKind.Utf8Bom => "UTF-8 with BOM",
        TextEncodingKind.Utf16LeBom => "UTF-16 LE",
        TextEncodingKind.Utf16BeBom => "UTF-16 BE",
        TextEncodingKind.Utf32LeBom => "UTF-32 LE",
        TextEncodingKind.Cp949 => "Korean (CP949)",
        _ => kind.ToString(),
    };
}
