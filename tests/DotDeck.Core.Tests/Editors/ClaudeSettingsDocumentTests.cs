using DotDeck.Core.Hooks;
using DotDeck.Core.Editors;

namespace DotDeck.Core.Tests.Editors;

public sealed class ClaudeSettingsDocumentTests
{
    [Fact]
    public void Unchanged_nonstring_env_values_keep_their_original_json_types()
    {
        var doc = ClaudeSettingsDocument.TryParse("""{"env":{"number":42,"object":{"keep":true},"empty":null,"text":"old"}}""", out _)!;
        var values = doc.GetEnv().Select(p => p.Key == "text" ? KeyValuePair.Create(p.Key, "new") : p).ToList();
        doc.SetEnv(values);
        var saved = System.Text.Json.Nodes.JsonNode.Parse(doc.ToJson())!["env"]!;
        Assert.Equal(42, saved["number"]!.GetValue<int>());
        Assert.True(saved["object"]!["keep"]!.GetValue<bool>());
        Assert.Null(saved["empty"]);
        Assert.Equal("new", saved["text"]!.GetValue<string>());
        doc.SetEnv([KeyValuePair.Create("number", "changed")]);
        Assert.Equal("changed", System.Text.Json.Nodes.JsonNode.Parse(doc.ToJson())!["env"]!["number"]!.GetValue<string>());
    }

    private const string Settings = """
        {
          "model": "opus",
          "permissions": {
            "allow": ["Bash(git:*)", "Read"],
            "deny": ["Read"],
            "defaultMode": "plan"
          },
          "hooks": {
            "PreToolUse": [
              { "matcher": "Bash", "note": "g", "hooks": [ { "type": "command", "command": "python C:\\h\\guard.py", "timeout": 10, "x": 1 } ] }
            ],
            "Stop": [ { "hooks": [ { "type": "command", "command": "echo done" } ] } ]
          },
          "env": { "A": "1", "N": 2 },
          "futureKey": { "deep": [1, 2] }
        }
        """;

    private static ClaudeSettingsDocument Load(string text = Settings) =>
        ClaudeSettingsDocument.TryParse(text, out var error) ?? throw new InvalidOperationException(error);

    [Fact]
    public void Reads_permissions_hooks_and_env()
    {
        var doc = Load();

        Assert.Equal(["Bash(git:*)", "Read"], doc.GetPermissions("allow"));
        Assert.Empty(doc.GetPermissions("ask"));
        var hooks = doc.GetHooks();
        Assert.Equal(2, hooks.Count);
        Assert.Equal(new[] { "PreToolUse", "Bash", @"python C:\h\guard.py" }, new[] { hooks[0].Event, hooks[0].Matcher, hooks[0].Command });
        Assert.Equal(10, hooks[0].Timeout);
        Assert.Equal(string.Empty, hooks[1].Matcher);
        Assert.Equal([new("A", "1"), new("N", "2")], doc.GetEnv());
    }

    [Fact]
    public void Writing_form_sections_keeps_unknown_keys()
    {
        var doc = Load();
        var hooks = doc.GetHooks().ToList();
        hooks[0] = hooks[0] with { Command = "python C:\\h\\guard2.py", Timeout = null };
        hooks.Add(new HookHandlerModel("PreToolUse", "Bash", "echo second"));

        doc.SetPermissions("allow", ["Read"]);
        doc.SetPermissions("ask", ["WebFetch"]);
        doc.SetHooks(hooks);
        doc.SetEnv([new("A", "2")]);

        var again = Load(doc.ToJson());
        Assert.Equal(["Read"], again.GetPermissions("allow"));
        Assert.Equal(["WebFetch"], again.GetPermissions("ask"));
        var json = doc.ToJson();
        Assert.Contains("\"defaultMode\": \"plan\"", json, StringComparison.Ordinal);
        Assert.Contains("\"futureKey\"", json, StringComparison.Ordinal);
        Assert.Contains("\"note\": \"g\"", json, StringComparison.Ordinal);
        Assert.Contains("\"x\": 1", json, StringComparison.Ordinal);

        var rows = again.GetHooks();
        Assert.Equal(3, rows.Count);
        Assert.Null(rows[0].Timeout);
        Assert.Equal("echo second", rows[1].Command);
        Assert.Equal(rows[0].GroupExtra!.ToJsonString(), rows[1].GroupExtra!.ToJsonString());
        Assert.Equal([new("A", "2")], again.GetEnv());
    }

    [Fact]
    public void Unchanged_sections_round_trip_semantically()
    {
        var doc = Load();
        doc.SetHooks(doc.GetHooks());
        doc.SetEnv(doc.GetEnv());
        foreach (var kind in ClaudeSettingsDocument.PermissionKinds)
        {
            doc.SetPermissions(kind, doc.GetPermissions(kind));
        }

        Assert.Equal(
            System.Text.Json.Nodes.JsonNode.Parse(Load().ToJson())!.ToJsonString(),
            System.Text.Json.Nodes.JsonNode.Parse(doc.ToJson())!.ToJsonString());
    }

    [Fact]
    public void Empty_sections_are_removed()
    {
        var doc = Load();
        doc.SetHooks([]);
        doc.SetEnv([]);
        doc.SetPermissions("allow", []);
        doc.SetPermissions("deny", []);

        var json = doc.ToJson();
        Assert.DoesNotContain("\"hooks\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"env\"", json, StringComparison.Ordinal);
        Assert.Contains("\"defaultMode\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[1]")]
    [InlineData("{ \"a\": }")]
    [InlineData("{ \"a\": 1, \"a\": 2 }")]
    public void Invalid_json_is_reported(string text)
    {
        Assert.Null(ClaudeSettingsDocument.TryParse(text, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Comments_trailing_commas_and_empty_text_are_accepted()
    {
        Assert.NotNull(ClaudeSettingsDocument.TryParse("// c\n{ \"a\": 1, }", out _));
        Assert.Equal("{}", ClaudeSettingsDocument.TryParse("  ", out _)!.ToJson());
    }

    [Fact]
    public void Validation_finds_conflicts_duplicates_and_hook_problems()
    {
        var doc = Load();
        doc.SetPermissions("ask", ["WebFetch", "WebFetch"]);
        var hooks = doc.GetHooks().ToList();
        hooks.Add(new HookHandlerModel("OnSave", "", " ", 0));
        doc.SetHooks(hooks);

        var issues = doc.Validate(path => !path.EndsWith("guard.py", StringComparison.Ordinal));

        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Message.Contains("allow와 deny", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("ask에 중복", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Message.Contains("OnSave", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Message.Contains("command가 비어", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Message.Contains("timeout", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Message.Contains(@"C:\h\guard.py", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(@"python C:\h\guard.py --x", @"C:\h\guard.py")]
    [InlineData(@"""C:\Program Files\x\run.ps1""", @"C:\Program Files\x\run.ps1")]
    [InlineData(@"powershell -File C:\a\b.ps1", @"C:\a\b.ps1")]
    [InlineData(@"python $CLAUDE_PROJECT_DIR\x.py", null)]
    [InlineData(@"C:\tools\x.exe --flag", null)]
    [InlineData("echo hi", null)]
    public void Script_path_detection(string command, string? expected)
    {
        Assert.Equal(expected, ClaudeSettingsDocument.ScriptPath(command));
    }
}
