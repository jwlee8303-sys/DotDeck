using System.Text;

namespace DotDeck.Core.Files;

public static class FileStoreExtensions
{
    private static readonly UTF8Encoding s_utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // Invalid bytes throw instead of becoming U+FFFD, so damaged files are detected rather than silently rewritten.
    private static readonly UTF8Encoding s_strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Writes UTF-8 (no BOM) text through a temporary file in the same directory, then moves it over the target
    /// (same-volume rename, so readers never see a partial file). Used for app-owned data files such as projects.json.
    /// </summary>
    public static void WriteAllTextAtomic(this IFileStore store, string path, string text)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(path);

        var directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException("Path has no directory.", nameof(path));
        store.CreateDirectory(directory);

        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            store.WriteAllBytes(temp, s_utf8NoBom.GetBytes(text));
            store.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(store, temp);
            throw;
        }
    }

    /// <summary>Reads UTF-8 text (BOM optional). Throws <see cref="DecoderFallbackException"/> on invalid UTF-8.</summary>
    public static string ReadAllText(this IFileStore store, string path)
    {
        ArgumentNullException.ThrowIfNull(store);
        var bytes = store.ReadAllBytes(path);
        var start = bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? 3 : 0;
        return s_strictUtf8.GetString(bytes, start, bytes.Length - start);
    }

    private static void TryDelete(IFileStore store, string path)
    {
        try
        {
            if (store.FileExists(path))
            {
                store.DeleteFile(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
