namespace DotDeck.Core.Usage;

/// <summary>Sums token records of one local day: total, cache reads, per hour and the session that used the most.</summary>
public static class UsageAggregator
{
    /// <summary>
    /// Records of <paramref name="day"/> in <paramref name="zone"/>, each response counted once (first record of an id
    /// wins). The top session is the one with the largest total; its project is the first known project path.
    /// </summary>
    public static TokenUsageSummary Summarize(IEnumerable<TokenRecord> records, DateOnly day, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(zone);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var hourly = new long[24];
        long total = 0, cacheRead = 0;
        var sessions = new Dictionary<string, (long Total, string? Project)>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            var local = TimeZoneInfo.ConvertTime(record.Timestamp, zone);
            if (DateOnly.FromDateTime(local.DateTime) != day || !seen.Add(record.Id))
            {
                continue;
            }

            total += record.Total;
            cacheRead += record.CacheRead;
            hourly[local.Hour] += record.Total;
            sessions.TryGetValue(record.SessionId, out var session);
            sessions[record.SessionId] = (session.Total + record.Total, session.Project ?? record.ProjectPath);
        }

        if (sessions.Count == 0)
        {
            return TokenUsageSummary.Empty;
        }

        var top = sessions.MaxBy(s => s.Value.Total).Value;
        return new TokenUsageSummary(total, cacheRead, hourly, top.Project, top.Total, sessions.Count);
    }
}
