using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using DotDeck.Core.Files;
using DotDeck.Core.Security;

namespace DotDeck.Core.SystemConfig;

/// <summary>One variable change. Null old value = added, null new value = removed.</summary>
public sealed record EnvironmentChange(string Name, string? OldValue, string? NewValue)
{
    public bool IsSecret => SecretMasker.IsSecretName(Name);
}

/// <summary>Change record written to the backup folder. Secret values are stored masked (not revertible).</summary>
public sealed record EnvironmentChangeRecord(string Id, DateTimeOffset CreatedAt, EnvironmentScope Scope, IReadOnlyList<EnvironmentChange> Changes);

/// <summary>Computes, previews, applies, records and reverts environment variable edits (SPEC §8).</summary>
public static class EnvironmentChanges
{
    public const string FilePrefix = "env-";

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    public static IReadOnlyList<EnvironmentChange> Compute(IReadOnlyDictionary<string, string> before, IReadOnlyList<KeyValuePair<string, string>> after)
    {
        var target = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in after.Where(p => p.Key.Trim().Length > 0))
        {
            target[name.Trim()] = value;
        }

        var changes = new List<EnvironmentChange>();
        foreach (var (name, old) in before)
        {
            if (!target.TryGetValue(name, out var value))
            {
                changes.Add(new EnvironmentChange(name, old, null));
            }
            else if (!string.Equals(old, value, StringComparison.Ordinal))
            {
                changes.Add(new EnvironmentChange(name, old, value));
            }
        }

        foreach (var (name, value) in target.Where(p => !before.Keys.Contains(p.Key, StringComparer.OrdinalIgnoreCase)))
        {
            changes.Add(new EnvironmentChange(name, null, value));
        }

        return changes.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>"NAME=value" lines for the diff preview; secret values are masked.</summary>
    public static (string Before, string After) PreviewText(IReadOnlyList<EnvironmentChange> changes)
    {
        static string Line(EnvironmentChange c, string? v) => v is null ? string.Empty : $"{c.Name}={(c.IsSecret ? SecretMasker.Mask + (v.Length > 0 ? $" ({v.GetHashCode(StringComparison.Ordinal):X8})" : string.Empty) : v)}\n";
        var before = new StringBuilder();
        var after = new StringBuilder();
        foreach (var c in changes)
        {
            before.Append(Line(c, c.OldValue));
            after.Append(Line(c, c.NewValue));
        }

        return (before.ToString(), after.ToString());
    }

    /// <summary>
    /// Names whose current value is no longer the change's old value (changed elsewhere since it was read).
    /// </summary>
    public static IReadOnlyList<string> Conflicts(IReadOnlyDictionary<string, string> current, IReadOnlyList<EnvironmentChange> changes) =>
        changes.Where(c => !string.Equals(current.TryGetValue(c.Name, out var v) ? v : null, c.OldValue, StringComparison.Ordinal))
            .Select(c => c.Name).ToList();

    /// <summary>
    /// Writes a masked record to <paramref name="backupsDir"/> first (nothing is changed if that fails), then
    /// applies the changes. When a write fails, the record is narrowed to the changes applied so far (so they
    /// can be reverted) and the error is rethrown.
    /// </summary>
    public static EnvironmentChangeRecord Apply(IEnvironmentStore env, EnvironmentScope scope, IReadOnlyList<EnvironmentChange> changes,
        IFileStore store, string backupsDir, TimeProvider time)
    {
        var now = time.GetUtcNow();
        var id = $"{FilePrefix}{now:yyyyMMdd-HHmmss-fff}";
        var record = Record(id, now, scope, changes, store, backupsDir);
        var applied = new List<EnvironmentChange>();
        try
        {
            foreach (var change in changes)
            {
                env.Set(scope, change.Name, change.NewValue);
                applied.Add(change);
            }
        }
        catch
        {
            if (applied.Count == 0)
            {
                store.DeleteFile(Path.Combine(backupsDir, id + ".json"));
            }
            else
            {
                Record(id, now, scope, applied, store, backupsDir);
            }

            throw;
        }
        finally
        {
            if (applied.Count > 0)
            {
                env.Broadcast();
            }
        }

        return record;
    }

    private static EnvironmentChangeRecord Record(string id, DateTimeOffset now, EnvironmentScope scope, IReadOnlyList<EnvironmentChange> changes, IFileStore store, string backupsDir)
    {
        var record = new EnvironmentChangeRecord(
            id, now, scope,
            changes.Select(c => c.IsSecret ? c with { OldValue = Masked(c.OldValue), NewValue = Masked(c.NewValue) } : c).ToList());
        store.CreateDirectory(backupsDir);
        store.WriteAllTextAtomic(Path.Combine(backupsDir, id + ".json"), JsonSerializer.Serialize(record, s_json));
        return record;
    }

    public static IReadOnlyList<EnvironmentChangeRecord> History(IFileStore store, string backupsDir)
    {
        if (!store.DirectoryExists(backupsDir))
        {
            return [];
        }

        var result = new List<EnvironmentChangeRecord>();
        foreach (var entry in store.GetChildren(backupsDir).Where(e => !e.IsDirectory && e.Name.StartsWith(FilePrefix, StringComparison.Ordinal) && e.Name.EndsWith(".json", StringComparison.Ordinal)))
        {
            try
            {
                if (JsonSerializer.Deserialize<EnvironmentChangeRecord>(store.ReadAllText(entry.FullPath)) is { } record)
                {
                    result.Add(record);
                }
            }
            catch (JsonException)
            {
            }
        }

        return result.OrderByDescending(r => r.CreatedAt).ToList();
    }

    /// <summary>Changes that undo <paramref name="record"/>. Secret entries cannot be restored and are skipped.</summary>
    public static IReadOnlyList<EnvironmentChange> Revert(EnvironmentChangeRecord record, out int skippedSecrets)
    {
        skippedSecrets = record.Changes.Count(c => c.IsSecret);
        return record.Changes.Where(c => !c.IsSecret).Select(c => new EnvironmentChange(c.Name, c.NewValue, c.OldValue)).ToList();
    }

    private static string? Masked(string? value) => value is null ? null : SecretMasker.Mask;
}
