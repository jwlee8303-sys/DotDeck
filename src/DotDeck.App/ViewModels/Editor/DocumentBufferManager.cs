using DotDeck.Core.Documents;

namespace DotDeck.App.ViewModels.Editor;

/// <summary>One <see cref="DocumentBuffer"/> per file path, reference-counted by the tabs that show it.</summary>
/// <param name="wordWrapDefault">Soft wrap a new buffer starts with, by path (null: off).</param>
public sealed class DocumentBufferManager(TextFileReader reader, Func<string, bool>? wordWrapDefault = null)
{
    private readonly TextFileReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    private readonly Dictionary<string, DocumentBuffer> _buffers = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<DocumentBuffer> Buffers => _buffers.Values;

    public TextFileReader Reader => _reader;

    public DocumentBuffer? Find(string path) => _buffers.GetValueOrDefault(Normalize(path));

    /// <summary>
    /// Returns the open buffer for <paramref name="path"/> or reads the file on the thread pool and creates one
    /// on the calling (UI) thread. The caller owns one reference and must <see cref="Release"/> it.
    /// </summary>
    public async Task<DocumentBuffer> AcquireAsync(string path)
    {
        var key = Normalize(path);
        if (!_buffers.TryGetValue(key, out var buffer))
        {
            var snapshot = await Task.Run(() => _reader.Read(key)).ConfigureAwait(true);

            // Another open of the same file may have finished while this one was reading.
            if (!_buffers.TryGetValue(key, out buffer))
            {
                buffer = new DocumentBuffer(snapshot) { DefaultWordWrap = wordWrapDefault?.Invoke(key) ?? false };
                _buffers[key] = buffer;
            }
        }

        buffer.ReferenceCount++;
        return buffer;
    }

    /// <summary>Takes another reference to an open buffer (second view of the same file).</summary>
    public void AddReference(DocumentBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        buffer.ReferenceCount++;
    }

    public void Release(DocumentBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (--buffer.ReferenceCount <= 0)
        {
            _buffers.Remove(Normalize(buffer.Path));
        }
    }

    private static string Normalize(string path) => System.IO.Path.GetFullPath(path);
}
