using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;

namespace DotDeck.Core.Editors;

/// <summary>One flattened hook command: <c>hooks.&lt;Event&gt;[i].hooks[j]</c>.</summary>
/// <param name="CommandExtra">The original command object (unknown fields are written back).</param>
/// <param name="GroupExtra">The original matcher group object (unknown fields are written back).</param>
public sealed record HookRule(
    string Event,
    string Matcher,
    string Command,
    int? Timeout = null,
    JsonObject? CommandExtra = null,
    JsonObject? GroupExtra = null);

/// <summary>
/// Claude Code settings.json as a JSON tree. The form reads and replaces only <c>permissions.allow|ask|deny</c>,
/// <c>hooks</c> and <c>env</c>; every other key (and unknown fields inside those sections) is kept as is.
/// </summary>
public sealed class ClaudeSettingsDocument
{
    public static readonly IReadOnlyList<string> PermissionKinds = ["allow", "ask", "deny"];

    public static readonly IReadOnlyList<string> HookEvents =
    [
        "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "Notification", "Stop", "SubagentStop",
        "PreCompact", "SessionEnd",
    ];

    private static readonly JsonSerializerOptions s_write = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private static readonly JsonDocumentOptions s_read = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private readonly JsonObject _root;

    private ClaudeSettingsDocument(JsonObject root) => _root = root;

    /// <summary>Parses settings text; an empty text is an empty object. Returns null with a message on error.</summary>
    public static ClaudeSettingsDocument? TryParse(string text, out string? error)
    {
        ArgumentNullException.ThrowIfNull(text);
        error = null;
        if (text.Trim().Length == 0)
        {
            return new ClaudeSettingsDocument([]);
        }

        try
        {
            if (JsonNode.Parse(text, documentOptions: s_read) is JsonObject obj)
            {
                _ = obj.Count; // surfaces duplicate keys
                return new ClaudeSettingsDocument(obj);
            }

            error = "최상위 값이 JSON 객체가 아닙니다.";
        }
        catch (JsonException ex)
        {
            error = $"JSON 오류: {ex.Message}";
        }
        catch (ArgumentException ex)
        {
            error = $"JSON 오류: {ex.Message}";
        }

        return null;
    }

    public IReadOnlyList<string> GetPermissions(string kind) =>
        _root["permissions"]?[kind] is JsonArray array
            ? array.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToJsonString() ?? "null").ToList()
            : [];

    public void SetPermissions(string kind, IReadOnlyList<string> rules)
    {
        if (!PermissionKinds.Contains(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (_root["permissions"] is not JsonObject permissions)
        {
            if (rules.Count == 0)
            {
                return;
            }

            permissions = [];
            _root["permissions"] = permissions;
        }

        if (rules.Count == 0)
        {
            permissions.Remove(kind);
            return;
        }

        permissions[kind] = new JsonArray(rules.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray());
    }

    public IReadOnlyList<HookRule> GetHooks()
    {
        var result = new List<HookRule>();
        if (_root["hooks"] is not JsonObject hooks)
        {
            return result;
        }

        foreach (var (evt, groups) in hooks)
        {
            if (groups is not JsonArray groupArray)
            {
                continue;
            }

            foreach (var group in groupArray.OfType<JsonObject>())
            {
                var matcher = group["matcher"] is JsonValue m && m.TryGetValue<string>(out var ms) ? ms : string.Empty;
                if (group["hooks"] is not JsonArray commands)
                {
                    continue;
                }

                foreach (var command in commands.OfType<JsonObject>())
                {
                    var text = command["command"] is JsonValue c && c.TryGetValue<string>(out var cs) ? cs : string.Empty;
                    int? timeout = command["timeout"] is JsonValue t && t.TryGetValue<int>(out var ti) ? ti : null;
                    result.Add(new HookRule(evt, matcher, text, timeout, (JsonObject)command.DeepClone(), (JsonObject)group.DeepClone()));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Replaces <c>hooks</c>. Rows of the same event and matcher (in order of first appearance) share one group;
    /// unknown fields of the original group/command objects are kept. Events not present in any row are removed.
    /// </summary>
    public void SetHooks(IReadOnlyList<HookRule> rules)
    {
        if (rules.Count == 0)
        {
            _root.Remove("hooks");
            return;
        }

        var hooks = new JsonObject();
        foreach (var byEvent in rules.Where(r => r.Event.Trim().Length > 0).GroupBy(r => r.Event.Trim(), StringComparer.Ordinal))
        {
            var groups = new JsonArray();
            foreach (var byMatcher in byEvent.GroupBy(r => r.Matcher.Trim(), StringComparer.Ordinal))
            {
                var group = (JsonObject?)byMatcher.First().GroupExtra?.DeepClone() ?? [];
                if (byMatcher.Key.Length > 0)
                {
                    group["matcher"] = byMatcher.Key;
                }
                else
                {
                    group.Remove("matcher");
                }

                var commands = new JsonArray();
                foreach (var rule in byMatcher)
                {
                    var command = (JsonObject?)rule.CommandExtra?.DeepClone() ?? [];
                    command["type"] ??= "command";
                    command["command"] = rule.Command;
                    if (rule.Timeout is { } timeout)
                    {
                        command["timeout"] = timeout;
                    }
                    else
                    {
                        command.Remove("timeout");
                    }

                    commands.Add(command);
                }

                group["hooks"] = commands;
                groups.Add(group);
            }

            hooks[byEvent.Key] = groups;
        }

        // Keep unknown non-array members of the old hooks object (e.g. future settings).
        if (_root["hooks"] is JsonObject old)
        {
            foreach (var (key, value) in old.Where(p => p.Value is not JsonArray).ToList())
            {
                hooks[key] = value?.DeepClone();
            }
        }

        _root["hooks"] = hooks;
    }

    public IReadOnlyList<KeyValuePair<string, string>> GetEnv() =>
        _root["env"] is JsonObject env
            ? env.Select(p => new KeyValuePair<string, string>(p.Key, p.Value is JsonValue v && v.TryGetValue<string>(out var s) ? s : p.Value?.ToJsonString() ?? string.Empty)).ToList()
            : [];

    public void SetEnv(IReadOnlyList<KeyValuePair<string, string>> values)
    {
        var env = new JsonObject();
        foreach (var (key, value) in values.Where(p => p.Key.Trim().Length > 0))
        {
            env[key.Trim()] = value;
        }

        if (env.Count == 0)
        {
            _root.Remove("env");
        }
        else
        {
            _root["env"] = env;
        }
    }

    public string ToJson() => _root.ToJsonString(s_write);

    /// <summary>Duplicate and conflicting permission rules, hook events and commands (SPEC §7).</summary>
    public IReadOnlyList<ValidationIssue> Validate(Func<string, bool>? fileExists = null)
    {
        var issues = new List<ValidationIssue>();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kind in PermissionKinds)
        {
            foreach (var rule in GetPermissions(kind))
            {
                if (seen.TryGetValue(rule, out var other))
                {
                    issues.Add(other == kind
                        ? new ValidationIssue(IssueSeverity.Warning, $"권한 규칙이 {kind}에 중복됩니다: {rule}")
                        : new ValidationIssue(IssueSeverity.Error, $"권한 규칙이 {other}와 {kind}에 모두 있습니다: {rule}"));
                }
                else
                {
                    seen[rule] = kind;
                }
            }
        }

        foreach (var hook in GetHooks())
        {
            if (!HookEvents.Contains(hook.Event, StringComparer.Ordinal))
            {
                issues.Add(new ValidationIssue(IssueSeverity.Warning, $"알려지지 않은 훅 이벤트입니다: {hook.Event}"));
            }

            if (hook.Command.Trim().Length == 0)
            {
                issues.Add(new ValidationIssue(IssueSeverity.Error, $"{hook.Event} 훅의 command가 비어 있습니다."));
            }
            else if (fileExists is not null && ScriptPath(hook.Command) is { } script && !fileExists(script))
            {
                issues.Add(new ValidationIssue(IssueSeverity.Warning, $"훅 스크립트를 찾을 수 없습니다: {script}"));
            }

            if (hook.Timeout is <= 0)
            {
                issues.Add(new ValidationIssue(IssueSeverity.Error, $"{hook.Event} 훅의 timeout은 양수여야 합니다."));
            }
        }

        return issues;
    }

    /// <summary>
    /// The script file a hook command runs, when it names one by absolute path (first token, or the first token
    /// after an interpreter such as python/node/bash/powershell). Paths with variables are not checked.
    /// </summary>
    public static string? ScriptPath(string command)
    {
        var tokens = Tokenize(command).ToList();
        foreach (var token in tokens.Take(3))
        {
            if (token.Contains('$', StringComparison.Ordinal) || token.Contains('%', StringComparison.Ordinal))
            {
                return null;
            }

            if (Path.IsPathFullyQualified(token)
                && Path.GetExtension(token) is { Length: > 0 } ext
                && !ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return token;
            }
        }

        return null;
    }

    private static IEnumerable<string> Tokenize(string command)
    {
        var i = 0;
        while (i < command.Length)
        {
            while (i < command.Length && char.IsWhiteSpace(command[i]))
            {
                i++;
            }

            if (i >= command.Length)
            {
                yield break;
            }

            if (command[i] is '"' or '\'')
            {
                var quote = command[i];
                var end = command.IndexOf(quote, i + 1);
                end = end < 0 ? command.Length : end;
                yield return command[(i + 1)..end];
                i = end + 1;
            }
            else
            {
                var start = i;
                while (i < command.Length && !char.IsWhiteSpace(command[i]))
                {
                    i++;
                }

                yield return command[start..i];
            }
        }
    }
}
