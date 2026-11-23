using System.Globalization;
using System.Text.Json;

namespace DotDeck.Core.Usage;

/// <summary>
/// Claude Code usage: tokens from <c>~/.claude/projects/**/*.jsonl</c> (sessions and their subagents) and the rate
/// limits DotDeck's status line script saved (<see cref="ClaudeStatusLineInstaller"/>). A response is logged once per
/// content block with the same usage, so records are keyed by message id and request id.
/// Total = input + output + cache creation; cache reads are reported apart.
/// </summary>
public sealed class ClaudeUsageReader(string claudeHome, string usageFile)
{
    private readonly LogFileCache<List<TokenRecord>> _cache = new(Parse);

    public string ProjectsDir => Path.Combine(claudeHome, "projects");

    /// <summary>Today's usage, or null when Claude Code has no session logs here. Throws on unreadable folders.</summary>
    public TokenUsageReport? Read(DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (!Directory.Exists(ProjectsDir))
        {
            return null;
        }

        var day = UsageDay.Of(now, zone);
        var files = LogFiles.RecentFiles(ProjectsDir, "*.jsonl", day.StartUtc);
        var records = _cache.Get(files).SelectMany(r => r);
        return new TokenUsageReport(UsageAggregator.Summarize(records, day.Date, zone), ReadLimits(usageFile));
    }

    internal static List<TokenRecord> Parse(string path)
    {
        var records = new List<TokenRecord>();
        foreach (var line in LogFiles.ReadLines(path))
        {
            if (!line.Contains("\"usage\"", StringComparison.Ordinal) || UsageJson.TryParse(line) is not { } doc)
            {
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (UsageJson.String(root, "type") != "assistant"
                    || !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                    || !message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object
                    || UsageJson.Time(root, "timestamp") is not { } time)
                {
                    continue;
                }

                var id = (UsageJson.String(message, "id") ?? Guid.NewGuid().ToString("N")) + ":" + UsageJson.String(root, "requestId");
                var total = UsageJson.Long(usage, "input_tokens") + UsageJson.Long(usage, "output_tokens") + UsageJson.Long(usage, "cache_creation_input_tokens");
                var session = UsageJson.String(root, "sessionId") ?? Path.GetFileNameWithoutExtension(path);
                records.Add(new TokenRecord(id, session, UsageJson.String(root, "cwd"), time, total, UsageJson.Long(usage, "cache_read_input_tokens")));
            }
        }

        return records;
    }

    /// <summary>
    /// The file the status line script writes: <c>{"measured_at": ms, "five_hour": {"used_percentage", "resets_at"},
    /// "seven_day": {…}}</c>. Null when it is missing or unreadable.
    /// </summary>
    internal static ServerLimits? ReadLimits(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || UsageJson.Epoch(root, "measured_at") is not { } measured)
            {
                return null;
            }

            static LimitWindow? Window(JsonElement root, string name) =>
                root.TryGetProperty(name, out var w) && w.ValueKind == JsonValueKind.Object && UsageJson.Double(w, "used_percentage") is { } used
                    ? new LimitWindow(used, UsageJson.Epoch(w, "resets_at"))
                    : null;

            return new ServerLimits(Window(root, "five_hour"), Window(root, "seven_day"), measured);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>The local day of a moment, and when it started in UTC.</summary>
internal readonly record struct UsageDay(DateOnly Date, DateTime StartUtc)
{
    /// <summary>
    /// Where daylight saving time begins at midnight, the day has no 00:00: it starts at the first valid local time
    /// (time zone offsets change in steps of at least 15 minutes).
    /// </summary>
    public static UsageDay Of(DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var date = DateOnly.FromDateTime(local.DateTime);
        var start = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        for (var i = 0; i < 96 && zone.IsInvalidTime(start); i++)
        {
            start = start.AddMinutes(15);
        }

        return new UsageDay(date, TimeZoneInfo.ConvertTimeToUtc(start, zone));
    }
}

/// <summary>Lenient readers for log JSON: a missing or mistyped value is null or 0, never an exception.</summary>
internal static class UsageJson
{
    public static JsonDocument? TryParse(string line)
    {
        try
        {
            return JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return null; // a partial last line while the CLI is writing
        }
    }

    public static string? String(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static long Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    public static double? Double(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    public static DateTimeOffset? Time(JsonElement e, string name) =>
        String(e, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    private const double MaxUnixSeconds = 253_402_300_799; // 9999-12-31T23:59:59Z

    /// <summary>Unix seconds or milliseconds (by magnitude), or an ISO 8601 string; null when out of range.</summary>
    public static DateTimeOffset? Epoch(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
        {
            return null;
        }

        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && n > 0)
        {
            var seconds = n >= 1e12 ? n / 1000 : n;
            return seconds <= MaxUnixSeconds ? DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)) : null;
        }

        if (v.ValueKind == JsonValueKind.Number)
        {
            return null;
        }

        return Time(e, name);
    }
}
