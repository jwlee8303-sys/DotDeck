using System.Text;
using DotDeck.Core.Explorer;
using DotDeck.Core.Files;

namespace DotDeck.Core.Documents;

/// <summary>A character the target encoding cannot represent (1-based line/column in the new text).</summary>
public sealed record UnencodableCharacter(int Line, int Column, string Text);

/// <summary>
/// Result of <see cref="SafeWriter.Prepare"/>: what would be written and the diff against the baseline.
/// <see cref="NewBytes"/> is null when <see cref="CanCommit"/> is false or the text is unchanged.
/// </summary>
public sealed record WritePlan(
    string Path,
    TextFileSnapshot? Baseline,
    string NewText,
    TextEncodingKind Encoding,
    byte[]? NewBytes,
    TextDiff Diff,
    bool IsUnchanged,
    bool LineEndingsChanged,
    IReadOnlyList<UnencodableCharacter> UnencodableCharacters,
    string? BlockingReason)
{
    public bool CanCommit => BlockingReason is null;
}

public enum ExternalChangeKind
{
    Modified,
    Deleted,
    Created,
}

/// <summary>The file changed on disk since it was read. Nothing was written.</summary>
public sealed class ExternalChangeException(string path, ExternalChangeKind kind)
    : IOException($"File changed on disk ({kind}): {path}")
{
    public string Path { get; } = path;

    public ExternalChangeKind Kind { get; } = kind;
}

/// <summary>The target is read-only. Nothing was written.</summary>
public sealed class ReadOnlyFileException(string path) : IOException($"File is read-only: {path}")
{
    public string Path { get; } = path;
}

/// <summary>Outcome of a commit: the snapshot now on disk and the backup taken (null for new files or no-op saves).</summary>
public sealed record SaveResult(TextFileSnapshot Snapshot, BackupSet? Backup, bool Written);

/// <summary>
/// Safe save pipeline: <see cref="Prepare"/> (encode + diff, no IO) → <see cref="Commit"/>
/// (external change check → backup → temp file in the same folder → atomic replace).
/// Saving unchanged text never touches the file.
/// </summary>
public sealed class SafeWriter(IFileStore store, BackupStore backups)
{
    private const int MaxReportedUnencodable = 20;

    private readonly IFileStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly BackupStore _backups = backups ?? throw new ArgumentNullException(nameof(backups));

    /// <summary>
    /// Plans writing <paramref name="newText"/> over the file described by <paramref name="baseline"/>
    /// (null for a new file). The baseline's encoding is kept unless <paramref name="encoding"/> overrides it.
    /// </summary>
    public static WritePlan Prepare(string path, TextFileSnapshot? baseline, string newText, TextEncodingKind? encoding = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(newText);
        if (baseline is not null && !string.Equals(System.IO.Path.GetFullPath(baseline.Path), System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Baseline describes a different file.", nameof(baseline));
        }

        var kind = encoding ?? baseline?.Encoding ?? TextEncodingKind.Utf8;
        var diff = TextDiffService.Compute(baseline?.Text ?? string.Empty, newText);
        var lineEndingsChanged = baseline is not null && LineEndingInfo.Detect(newText) is var now
            && (now.CrLfCount, now.LfCount, now.CrCount) != (baseline.LineEndings.CrLfCount, baseline.LineEndings.LfCount, baseline.LineEndings.CrCount);

        // Same text, same encoding: keep the file as is (byte-identical, even if it would not round-trip).
        var isUnchanged = baseline is not null && kind == baseline.Encoding && string.Equals(newText, baseline.Text, StringComparison.Ordinal);

        string? blocking = null;
        if (SecretFilePolicy.IsSecret(System.IO.Path.GetFileName(path)))
        {
            blocking = "Secret files cannot be saved.";
        }
        else if (baseline?.IsReadOnly == true)
        {
            blocking = baseline.ReadOnlyReason;
        }
        else if (baseline is { HasDecodingErrors: true } && encoding is null && !isUnchanged)
        {
            blocking = "The file contains bytes that could not be decoded; saving would corrupt them. Choose an encoding explicitly.";
        }

        var unencodable = FindUnencodable(newText, kind);
        if (blocking is null && unencodable.Count > 0)
        {
            blocking = $"{TextEncodings.DisplayName(kind)} cannot represent some characters.";
        }

        // Unchanged plans carry no bytes: Commit is a no-op unless the user overwrites an external change.
        var bytes = blocking is null && !isUnchanged ? TextEncodings.Encode(newText, kind) : null;
        return new WritePlan(path, baseline, newText, kind, bytes, diff, isUnchanged, lineEndingsChanged, unencodable, blocking);
    }

    /// <summary>
    /// Writes the plan. Throws <see cref="ExternalChangeException"/> when the file differs from the baseline
    /// (unless <paramref name="overwriteExternalChanges"/>), <see cref="ReadOnlyFileException"/>, or IO errors.
    /// On any failure the original file is untouched and the temp file is removed.
    /// </summary>
    public SaveResult Commit(WritePlan plan, bool overwriteExternalChanges = false)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CanCommit || (plan.NewBytes is null && !plan.IsUnchanged))
        {
            throw new InvalidOperationException(plan.BlockingReason ?? "Plan cannot be committed.");
        }

        var path = plan.Path;
        EnsureNotSecretTarget(path);
        var entry = _store.GetEntry(path);
        if (entry is { IsDirectory: true })
        {
            throw new IOException($"A folder exists at {path}.");
        }

        byte[]? current = entry is null ? null : _store.ReadAllBytes(path);
        if (!overwriteExternalChanges)
        {
            CheckExternalChange(path, plan.Baseline, current);
        }

        if (plan.IsUnchanged && current is not null && plan.Baseline is not null
            && string.Equals(TextFileReader.ComputeHash(current), plan.Baseline.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return new SaveResult(plan.Baseline, null, Written: false);
        }

        if (entry is { IsReadOnly: true })
        {
            throw new ReadOnlyFileException(path);
        }

        var bytes = plan.NewBytes ?? TextEncodings.Encode(plan.NewText, plan.Encoding);
        var backup = current is null ? null : _backups.Create([new BackupSource(path, current)], "save");

        // Re-check right before replacing so a change made while backing up is not silently overwritten.
        var expected = current is null ? null : TextFileReader.ComputeHash(current);
        WriteAtomic(path, bytes, replace: current is not null, expected);

        var mtime = _store.GetEntry(path)?.LastWriteTimeUtc ?? DateTime.UtcNow;
        return new SaveResult(TextFileReader.ForWrittenText(path, plan.NewText, plan.Encoding, bytes, mtime), backup, Written: true);
    }

    /// <summary>
    /// Restores a backed-up file. The current file (if any) is backed up first, so a restore can itself be undone.
    /// </summary>
    public SaveResult Restore(BackupSet set, BackupFile file)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(file);
        var bytes = _backups.ReadContent(set, file);
        var path = file.OriginalPath;
        EnsureNotSecretTarget(path);

        // Decode before touching the target so an undecodable backup fails without side effects.
        var restored = TextFileReader.Decode(path, bytes, DateTime.MinValue);

        var entry = _store.GetEntry(path);
        if (entry is { IsDirectory: true })
        {
            throw new IOException($"A folder exists at {path}.");
        }

        if (entry is { IsReadOnly: true })
        {
            throw new ReadOnlyFileException(path);
        }

        BackupSet? before = null;
        string? expected = null;
        if (entry is not null)
        {
            var current = _store.ReadAllBytes(path);
            before = _backups.Create([new BackupSource(path, current)], "before-restore");
            expected = TextFileReader.ComputeHash(current);
        }
        else
        {
            var directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                _store.CreateDirectory(directory);
            }
        }

        WriteAtomic(path, bytes, replace: entry is not null, expected);
        var mtime = _store.GetEntry(path)?.LastWriteTimeUtc ?? DateTime.UtcNow;
        return new SaveResult(restored with { LastWriteTimeUtc = mtime }, before, Written: true);
    }

    private static void CheckExternalChange(string path, TextFileSnapshot? baseline, byte[]? current)
    {
        if (baseline is null)
        {
            if (current is not null)
            {
                throw new ExternalChangeException(path, ExternalChangeKind.Created);
            }

            return;
        }

        if (current is null)
        {
            throw new ExternalChangeException(path, ExternalChangeKind.Deleted);
        }

        if (!string.Equals(TextFileReader.ComputeHash(current), baseline.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ExternalChangeException(path, ExternalChangeKind.Modified);
        }
    }

    // Checked again at commit time (plans are public records), and a link named "notes.txt" pointing at
    // ".env" must not be read, backed up or overwritten either.
    private void EnsureNotSecretTarget(string path)
    {
        if (SecretFilePolicy.IsSecret(System.IO.Path.GetFileName(path))
            || _store.ResolveLinkTarget(path) is { } target && SecretFilePolicy.IsSecret(System.IO.Path.GetFileName(target)))
        {
            throw new SecretFileException(path);
        }
    }

    private void WriteAtomic(string path, byte[] bytes, bool replace, string? expectedHash)
    {
        var directory = System.IO.Path.GetDirectoryName(path)
            ?? throw new ArgumentException("Path has no directory.", nameof(path));
        var temp = System.IO.Path.Combine(directory, $".{System.IO.Path.GetFileName(path)}.{Guid.NewGuid():N}.dotdeck.tmp");
        try
        {
            _store.WriteAllBytes(temp, bytes);
            if (replace && expectedHash is not null
                && !string.Equals(TextFileReader.ComputeHash(_store.ReadAllBytes(path)), expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new ExternalChangeException(path, ExternalChangeKind.Modified);
            }

            if (replace)
            {
                _store.ReplaceFile(temp, path);
            }
            else
            {
                _store.Move(temp, path);
            }
        }
        catch
        {
            try
            {
                if (_store.FileExists(temp))
                {
                    _store.DeleteFile(temp);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            throw;
        }
    }

    private static List<UnencodableCharacter> FindUnencodable(string text, TextEncodingKind kind)
    {
        var result = new List<UnencodableCharacter>();
        try
        {
            TextEncodings.GetStrict(kind).GetByteCount(text);
            return result;
        }
        catch (EncoderFallbackException)
        {
        }

        var encoder = TextEncodings.GetStrict(kind);
        int line = 1, column = 1;
        for (var i = 0; i < text.Length && result.Count < MaxReportedUnencodable; i++)
        {
            var length = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            var piece = text.Substring(i, length);
            try
            {
                encoder.GetByteCount(piece);
            }
            catch (EncoderFallbackException)
            {
                result.Add(new UnencodableCharacter(line, column, piece));
            }

            if (text[i] == '\n' || (text[i] == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n')))
            {
                line++;
                column = 1;
            }
            else
            {
                column += length;
            }

            i += length - 1;
        }

        return result;
    }
}
