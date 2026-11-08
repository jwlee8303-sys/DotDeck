using DotDeck.Core.Hooks;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;

namespace DotDeck.Core.Editors;

/// <summary>
/// Claude Code settings.json as a JSON tree. The form reads and replaces only <c>permissions.allow|ask|deny</c>,
/// <c>hooks</c> and <c>env</c>; every other key (and unknown fields inside those sections) is kept as is.
/// </summary>
public sealed class ClaudeSettingsDocument
{
    public static readonly IReadOnlyList<string> PermissionKinds = ["allow", "ask", "deny"];

    /// <summary>Hook event names of the built-in catalog (<c>Catalogs/Claude/hook-events.json</c>).</summary>
    public static IReadOnlyList<string> HookEvents => [.. HookEventCatalog.BuiltIn.Events.Select(e => e.Name)];

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
                foreach (var section in new[] { "permissions", "hooks", "env" })
                {
                    if (obj[section] is { } node && node is not JsonObject)
                    {
                        error = $"'{section}' 값이 객체가 아닙니다.";
                        return null;
                    }
                }

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
        _root["permissions"] is JsonObject permissions && permissions[kind] is JsonArray array
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

    public IReadOnlyList<HookHandlerModel> GetHooks()
    {
        var result = new List<HookHandlerModel>();
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

            var index = -1;
            foreach (var item in groupArray)
            {
                index++;
                if (item is not JsonObject group)
                {
                    continue;
                }

                var matcher = group["matcher"] is JsonValue m && m.TryGetValue<string>(out var ms) ? ms : string.Empty;
                if (group["hooks"] is not JsonArray commands)
                {
                    continue;
                }

                foreach (var command in commands.OfType<JsonObject>())
                {
                    var text = command["command"] is JsonValue c && c.TryGetValue<string>(out var cs) ? cs : string.Empty;
                    int? timeout = command["timeout"] is JsonValue t && t.TryGetValue<int>(out var ti) ? ti : null;
                    result.Add(new HookHandlerModel(evt, matcher, text, timeout, (JsonObject)command.DeepClone(), (JsonObject)group.DeepClone(), $"{evt}#{index}"));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Replaces the hook commands the form shows. Rows keep their original group (new rows of one event and matcher
    /// share a new group); unknown fields of groups and commands are kept, and so is everything the form cannot
    /// show (empty event arrays, non-object items, groups without a <c>hooks</c> array, non-object commands).
    /// </summary>
    public void SetHooks(IReadOnlyList<HookHandlerModel> rules)
    {
        var old = _root["hooks"] as JsonObject;
        var hooks = new JsonObject();
        foreach (var byEvent in rules.Where(r => r.Event.Trim().Length > 0).GroupBy(r => r.Event.Trim(), StringComparer.Ordinal))
        {
            var groups = new JsonArray();
            // New rows (no GroupKey) join the first existing group with the same matcher.
            var firstGroup = new Dictionary<string, (string Key, JsonObject? Extra)>(StringComparer.Ordinal);
            foreach (var r in byEvent.Where(r => r.GroupKey is not null))
            {
                firstGroup.TryAdd(r.Matcher, (r.GroupKey!, r.GroupExtra));
            }

            var resolved = byEvent.Select(r => r.GroupKey is null && firstGroup.TryGetValue(r.Matcher, out var g)
                ? r with { GroupKey = g.Key, GroupExtra = g.Extra }
                : r);
            foreach (var byMatcher in resolved.GroupBy(r => (Group: r.GroupKey ?? string.Empty, Matcher: r.Matcher)))
            {
                var byMatcherKey = byMatcher.Key.Matcher;
                var source = byMatcher.First().GroupExtra;
                var group = (JsonObject?)source?.DeepClone() ?? [];
                var hidden = (source?["hooks"] as JsonArray)?.Where(n => n is not JsonObject).Select(n => n?.DeepClone()).ToList() ?? [];
                if (byMatcherKey.Length > 0)
                {
                    group["matcher"] = byMatcherKey;
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

                    // http, prompt, agent and mcp_tool handlers have no command line: do not add one.
                    if (command["type"] is JsonValue type && type.TryGetValue<string>(out var typeName) && typeName == "command"
                        || command.ContainsKey("command") || rule.Command.Length > 0)
                    {
                        command["command"] = rule.Command;
                    }
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

                foreach (var node in hidden)
                {
                    commands.Add(node);
                }

                group["hooks"] = commands;
                groups.Add(group);
            }

            hooks[byEvent.Key] = groups;
        }

        if (old is not null)
        {
            foreach (var (key, value) in old.ToList())
            {
                if (value is not JsonArray array)
                {
                    // Unknown non-array member (e.g. a future setting).
                    hooks[key] = value?.DeepClone();
                    continue;
                }

                var kept = array.Where(n => n is not JsonObject g || g["hooks"] is not JsonArray cmds || !cmds.OfType<JsonObject>().Any()).Select(n => n?.DeepClone()).ToList();
                if (kept.Count == 0 && array.Count > 0)
                {
                    continue;
                }

                if (hooks[key] is not JsonArray target)
                {
                    target = [];
                    hooks[key] = target;
                }

                foreach (var node in kept)
                {
                    target.Add(node);
                }
            }
        }

        if (hooks.Count == 0 && old is null)
        {
            return;
        }

        if (hooks.Count == 0)
        {
            _root.Remove("hooks");
            return;
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

    /// <summary>Keys that do not apply (or need trust) at the file's level; see <see cref="SettingsScopeLint"/>.</summary>
    public IReadOnlyList<ValidationIssue> CheckScope(Roles.ConfigScope scope, SettingsKeyCatalog keys) => SettingsScopeLint.Check(_root, scope, keys);

    /// <summary>
    /// Duplicate and conflicting permission rules, hook events and commands (SPEC §7). With <paramref name="context"/>
    /// hook script paths are resolved from the settings file's location, and with <paramref name="keys"/> keys that
    /// do not apply at the file's level are reported.
    /// </summary>
    public IReadOnlyList<ValidationIssue> Validate(Func<string, bool>? fileExists = null, SettingsContext? context = null, SettingsKeyCatalog? keys = null,
        ClaudeToolCatalog? tools = null, HookEventCatalog? events = null)
    {
        var issues = new List<ValidationIssue>();
        if (context is not null && keys is not null)
        {
            issues.AddRange(SettingsScopeLint.Check(_root, context.Scope, keys));
        }

        var rules = PermissionKinds.SelectMany(kind => GetPermissions(kind).Select(rule => new PermissionEntry(kind, rule))).ToList();
        issues.AddRange(PermissionLint.Check(rules, tools).Select(i => i.Issue));

        events ??= HookEventCatalog.BuiltIn;
        foreach (var hook in GetHooks())
        {
            var info = events.Find(hook.Event);
            if (info is null)
            {
                issues.Add(new ValidationIssue(IssueSeverity.Warning, $"알려지지 않은 훅 이벤트입니다(값은 그대로 보존): {hook.Event}"));
            }
            else if (hook.Matcher.Trim() is { Length: > 0 } matcher && matcher != "*")
            {
                if (!info.UsesMatcher)
                {
                    issues.Add(new ValidationIssue(IssueSeverity.Info, $"{hook.Event} 이벤트는 matcher를 사용하지 않습니다(값 '{matcher}'은 무시됨, 보존)."));
                }
                else if (HookMatcher.Parse(matcher, HookMatcher.SyntaxOf(info)).RegexError() is { } regexError)
                {
                    issues.Add(new ValidationIssue(IssueSeverity.Warning, $"{hook.Event}: {regexError}"));
                }
            }

            // http, prompt, agent and mcp_tool hooks have no command line to check.
            var isCommand = hook.Type == "command";
            if (isCommand && hook.Command.Trim().Length == 0)
            {
                issues.Add(new ValidationIssue(IssueSeverity.Error, $"{hook.Event} 훅의 command가 비어 있습니다."));
            }
            else if (isCommand && fileExists is not null)
            {
                var check = HookScriptResolver.Check(hook.Command, hook.Args, context, fileExists);
                switch (check.Status)
                {
                    case HookScriptStatus.Missing:
                        issues.Add(new ValidationIssue(IssueSeverity.Warning,
                            $"훅 스크립트를 찾을 수 없습니다: {check.Path}{(check.Reason is null ? string.Empty : $" ({check.Reason})")}"));
                        break;
                    case HookScriptStatus.Unknown when context is not null:
                        issues.Add(new ValidationIssue(IssueSeverity.Info, $"훅 스크립트 경로 확인 불가: {check.Path} — {check.Reason}"));
                        break;
                }
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
    /// after an interpreter such as python/node/bash/powershell). Paths with variables are not resolved.
    /// </summary>
    public static string? ScriptPath(string command) =>
        HookScriptResolver.Check(command, null, null, _ => true) is { Status: HookScriptStatus.Exists or HookScriptStatus.Missing } check
            ? check.Path
            : null;
}
