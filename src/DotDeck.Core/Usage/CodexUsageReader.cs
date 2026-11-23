using System.Text.Json;

namespace DotDeck.Core.Usage;

/// <summary>
/// Codex CLI usage from <c>~/.codex/sessions/**/rollout-*.jsonl</c>: one <c>token_usage_record</c> per response
/// (keyed by response id), or, in logs written before those records existed, the growth of the cumulative
/// <c>token_count</c>. <c>cached_input_tokens</c> is part of <c>input_tokens</c> and reasoning is part of
/// <c>output_tokens</c>, so Total = (input − cached) + output and cache reads are reported apart. The rate limits are
/// the latest <c>token_count.rate_limits</c> the server sent, with the time of that event.
/// </summary>
public sealed class CodexUsageReader(string codexHome)
{
    private readonly LogFileCache<CodexLog> _cache = new(Parse);

    public string SessionsDir => Path.Combine(codexHome, "sessions");

    /// <summary>Today's usage, or null when Codex has no session logs here. Throws on unreadable folders.</summary>
    public TokenUsageReport? Read(DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (!Directory.Exists(SessionsDir))
        {
            return null;
        }

        var day = UsageDay.Of(now, zone);
        var all = new DirectoryInfo(SessionsDir)
            .EnumerateFiles("rollout-*.jsonl", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .ToList();
        // Today's logs for the tokens; the newest log too, so limits stay known on a day without Codex use.
        var files = all.Where(f => f.LastWriteTimeUtc >= day.StartUtc).ToList();
        if (files.Count == 0 && all.MaxBy(f => f.LastWriteTimeUtc) is { } newest)
        {
            files.Add(newest);
        }

        var logs = _cache.Get(files);
        var limits = logs.Select(l => l.Limits).Where(l => l is not null).MaxBy(l => l!.MeasuredAt);
        return new TokenUsageReport(UsageAggregator.Summarize(logs.SelectMany(l => l.Records), day.Date, zone), limits);
    }

    internal static CodexLog Parse(string path)
    {
        var records = new List<TokenRecord>();
        var fallback = new List<TokenRecord>();
        ServerLimits? limits = null;
        string? session = null, cwd = null;
        long lastCumulative = -1;
        var index = 0;
        foreach (var line in LogFiles.ReadLines(path))
        {
            index++;
            if (UsageJson.TryParse(line) is not { } doc)
            {
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var time = UsageJson.Time(root, "timestamp");
                switch (UsageJson.String(root, "type"))
                {
                    case "session_meta":
                        session ??= UsageJson.String(payload, "id") ?? UsageJson.String(payload, "session_id");
                        cwd = UsageJson.String(payload, "cwd") ?? cwd;
                        break;
                    case "turn_context":
                        cwd = UsageJson.String(payload, "cwd") ?? cwd;
                        break;
                    case "token_usage_record" when time is { } t && payload.TryGetProperty("usage", out var usage):
                        var id = UsageJson.String(payload, "response_id") ?? path + "#" + index;
                        var sessionId = UsageJson.String(payload, "session_id") ?? session ?? path;
                        records.Add(Record(id, sessionId, cwd, t, usage));
                        break;
                    case "event_msg" when UsageJson.String(payload, "type") == "token_count":
                        if (time is { } at && payload.TryGetProperty("rate_limits", out var rate) && ReadLimits(rate, at) is { } read
                            && (limits is null || read.MeasuredAt >= limits.MeasuredAt))
                        {
                            limits = read;
                        }

                        if (time is { } when && payload.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
                            && info.TryGetProperty("total_token_usage", out var cumulative) && info.TryGetProperty("last_token_usage", out var last))
                        {
                            // The same count is sent more than once; only growth is a new response.
                            var sum = UsageJson.Long(cumulative, "total_tokens");
                            if (sum != lastCumulative)
                            {
                                lastCumulative = sum;
                                fallback.Add(Record(path + "#" + index, session ?? path, cwd, when, last));
                            }
                        }

                        break;
                }
            }
        }

        return new CodexLog(records.Count > 0 ? records : fallback, limits);
    }

    private static TokenRecord Record(string id, string session, string? cwd, DateTimeOffset time, JsonElement usage)
    {
        var cached = UsageJson.Long(usage, "cached_input_tokens");
        var input = Math.Max(0, UsageJson.Long(usage, "input_tokens") - cached);
        return new TokenRecord(id, session, cwd, time, input + UsageJson.Long(usage, "output_tokens"), cached);
    }

    /// <summary>5-hour and weekly windows by their length (300 and 10080 minutes), else primary and secondary.</summary>
    internal static ServerLimits? ReadLimits(JsonElement rate, DateTimeOffset measuredAt)
    {
        if (rate.ValueKind != JsonValueKind.Object || (UsageJson.String(rate, "limit_id") is { } limitId && limitId != "codex"))
        {
            return null;
        }

        LimitWindow? five = null, seven = null;
        foreach (var name in new[] { "primary", "secondary" })
        {
            if (!rate.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object || UsageJson.Double(w, "used_percent") is not { } used)
            {
                continue;
            }

            var window = new LimitWindow(used, UsageJson.Epoch(w, "resets_at"));
            var minutes = UsageJson.Long(w, "window_minutes");
            if (minutes == 300 || (minutes == 0 && name == "primary"))
            {
                five = window;
            }
            else if (minutes == 10080 || (minutes == 0 && name == "secondary"))
            {
                seven = window;
            }
        }

        return five is null && seven is null ? null : new ServerLimits(five, seven, measuredAt);
    }
}

internal sealed record CodexLog(List<TokenRecord> Records, ServerLimits? Limits);
