namespace DotDeck.Core.Usage;

/// <summary>
/// Antigravity CLI (agy) activity from the write times of its conversation databases
/// (<c>~/.gemini/antigravity-cli/conversations/&lt;id&gt;.db</c> and their <c>-wal</c> files). The databases are never
/// opened: their token data is an undocumented binary format.
/// </summary>
public sealed class AgyActivityReader(string antigravityHome)
{
    public string ConversationsDir => Path.Combine(antigravityHome, "antigravity-cli", "conversations");

    /// <summary>Conversations written today and the last write, or null when agy has no conversations folder.</summary>
    public AgyActivity? Read(DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (!Directory.Exists(ConversationsDir))
        {
            return null;
        }

        var day = UsageDay.Of(now, zone);
        var latest = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in new DirectoryInfo(ConversationsDir).EnumerateFiles("*.db*"))
        {
            var id = file.Name.EndsWith(".db", StringComparison.OrdinalIgnoreCase) ? file.Name[..^3]
                : file.Name.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase) ? file.Name[..^7]
                : null;
            if (id is not null && (!latest.TryGetValue(id, out var known) || file.LastWriteTimeUtc > known))
            {
                latest[id] = file.LastWriteTimeUtc;
            }
        }

        DateTimeOffset? last = latest.Count == 0 ? null : new DateTimeOffset(latest.Values.Max(), TimeSpan.Zero);
        return new AgyActivity(latest.Values.Count(t => t >= day.StartUtc), last);
    }
}
