namespace DotDeck.Core.Usage;

/// <summary>
/// Tokens of one model response read from a CLI's local session log. <see cref="Total"/> leaves cache reads out
/// (they are reported apart in <see cref="CacheRead"/>). <see cref="Id"/> identifies the response across files, so a
/// response copied into a resumed or forked session is counted once.
/// </summary>
public sealed record TokenRecord(string Id, string SessionId, string? ProjectPath, DateTimeOffset Timestamp, long Total, long CacheRead);

/// <summary>One rate limit window as the provider's server reported it.</summary>
public sealed record LimitWindow(double UsedPercent, DateTimeOffset? ResetsAt)
{
    /// <summary>The used share at <paramref name="now"/>: a window whose reset time has passed starts again at 0.</summary>
    public double UsedPercentAt(DateTimeOffset now) => ResetsAt is { } reset && reset <= now ? 0 : UsedPercent;
}

/// <summary>
/// Rate limits last reported by the provider's server, and when DotDeck saw them (<see cref="MeasuredAt"/>): the
/// values are only as fresh as the CLI's last request.
/// </summary>
public sealed record ServerLimits(LimitWindow? FiveHour, LimitWindow? SevenDay, DateTimeOffset MeasuredAt);

/// <summary>Today's tokens of one CLI.</summary>
public sealed record TokenUsageSummary(
    long Total,
    long CacheRead,
    IReadOnlyList<long> Hourly,
    string? TopSessionProject,
    long TopSessionTotal,
    int SessionCount)
{
    public static TokenUsageSummary Empty { get; } = new(0, 0, new long[24], null, 0, 0);
}

/// <summary>Claude or Codex: today's tokens and the server limits when known.</summary>
public sealed record TokenUsageReport(TokenUsageSummary Today, ServerLimits? Limits);

/// <summary>agy: only conversation activity (its token data is not read).</summary>
public sealed record AgyActivity(int ConversationsToday, DateTimeOffset? LastUsedAt);
