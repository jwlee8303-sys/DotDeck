using DotDeck.Core.Editors;
using DotDeck.Core.Files;
using DotDeck.Core.Roles;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.Roles;

public sealed class EditorOverrideAndHookPathTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Overrides_are_saved_restored_and_reset()
    {
        var file = _temp.Combine("data", "editor-overrides.json");
        var target = _temp.Combine("p", "notes.json");
        var store = new EditorOverrideStore(PhysicalFileStore.Instance, file);
        Assert.Null(store.Get(target));

        store.Set(target, EditorKind.ClaudeSettings);
        var reloaded = new EditorOverrideStore(PhysicalFileStore.Instance, file);
        Assert.Equal(EditorKind.ClaudeSettings, reloaded.Get(target.ToUpperInvariant()));

        reloaded.Set(target, null);
        Assert.Null(new EditorOverrideStore(PhysicalFileStore.Instance, file).Get(target));
    }

    [Fact]
    public void Unknown_editor_names_are_kept_and_broken_files_are_ignored()
    {
        var file = _temp.Combine("editor-overrides.json");
        File.WriteAllText(file, "{\"schemaVersion\":1,\"overrides\":{\"C:\\\\a\\\\future.txt\":\"tomlForm\"}}");
        var store = new EditorOverrideStore(PhysicalFileStore.Instance, file);
        Assert.Null(store.Get(@"C:\a\future.txt"));

        store.Set(_temp.Combine("b.md"), EditorKind.Agent);
        Assert.Contains("tomlForm", File.ReadAllText(file), StringComparison.Ordinal);

        File.WriteAllText(file, "{ broken");
        Assert.Null(new EditorOverrideStore(PhysicalFileStore.Instance, file).Get(_temp.Combine("b.md")));
    }

    private static readonly SettingsContext Project = new(ConfigScope.Project, @"C:\home", @"D:\proj");
    private static readonly SettingsContext Global = new(ConfigScope.Global, @"C:\home", null);

    [Theory]
    [InlineData("python \"$CLAUDE_PROJECT_DIR/.claude/hooks/guard.py\"", @"D:\proj\.claude\hooks\guard.py")]
    [InlineData(@"python ${CLAUDE_PROJECT_DIR}\.claude\hooks\guard.py", @"D:\proj\.claude\hooks\guard.py")]
    [InlineData(@"pwsh -File $env:CLAUDE_PROJECT_DIR\h.ps1", @"D:\proj\h.ps1")]
    [InlineData(@"python .claude/hooks/guard.py", @"D:\proj\.claude\hooks\guard.py")]
    [InlineData(@"python ~/.claude/hooks/g.py", @"C:\home\.claude\hooks\g.py")]
    [InlineData(@"python %USERPROFILE%\.claude\hooks\g.py", @"C:\home\.claude\hooks\g.py")]
    [InlineData(@"bash /c/tools/g.sh", @"C:\tools\g.sh")]
    public void Project_hook_paths_are_resolved(string command, string expected)
    {
        var check = HookScriptResolver.Check(command, null, Project, p => p == expected);
        Assert.Equal(HookScriptStatus.Exists, check.Status);
        Assert.Equal(expected, check.Path);
    }

    [Theory]
    [InlineData(@"python $CLAUDE_PROJECT_DIR\x.py")]
    [InlineData(@"python .claude\hooks\x.py")]
    [InlineData(@"python $MY_TOOLS\x.py")]
    public void Unresolvable_global_paths_are_unknown(string command)
    {
        var check = HookScriptResolver.Check(command, null, Global, _ => false);
        Assert.Equal(HookScriptStatus.Unknown, check.Status);
        Assert.NotNull(check.Reason);
    }

    [Fact]
    public void Missing_inline_and_exec_form_commands()
    {
        Assert.Equal(HookScriptStatus.Missing, HookScriptResolver.Check(@"python C:\x\gone.py", null, Global, _ => false).Status);
        Assert.Equal(HookScriptStatus.NoScript, HookScriptResolver.Check("echo done >> log.txt", null, Project, _ => false).Status);
        Assert.Equal(HookScriptStatus.NoScript, HookScriptResolver.Check(@"C:\tools\x.exe --flag", null, Project, _ => false).Status);
        var exec = HookScriptResolver.Check("node", ["${CLAUDE_PROJECT_DIR}/hooks/a.js"], Project, _ => true);
        Assert.Equal(@"D:\proj\hooks\a.js", exec.Path);
    }

    [Fact]
    public void Validation_reports_scope_keys_and_hook_paths_by_context()
    {
        const string json = """
            {
              "modelPicker": {},
              "permissions": { "defaultMode": "bypassPermissions", "allow": ["Read"] },
              "hooks": {
                "Stop": [ { "hooks": [
                  { "type": "command", "command": "python $CLAUDE_PROJECT_DIR/h.py" },
                  { "type": "http", "url": "https://example.invalid/hook" }
                ] } ]
              }
            }
            """;
        var doc = ClaudeSettingsDocument.TryParse(json, out _)!;
        var keys = SettingsKeyCatalog.LoadClaude();

        var project = doc.Validate(_ => false, Project, keys);
        Assert.Contains(project, i => i.Message.Contains("modelPicker", StringComparison.Ordinal) && i.Severity == IssueSeverity.Warning);
        Assert.Contains(project, i => i.Message.Contains("bypassPermissions", StringComparison.Ordinal));
        Assert.Contains(project, i => i.Message.Contains("permissions.allow", StringComparison.Ordinal) && i.Severity == IssueSeverity.Info);
        Assert.Contains(project, i => i.Message.Contains(@"D:\proj\h.py", StringComparison.Ordinal));
        Assert.DoesNotContain(project, i => i.Message.Contains("command가 비어", StringComparison.Ordinal));

        var global = doc.Validate(_ => false, Global, keys);
        Assert.DoesNotContain(global, i => i.Message.Contains("modelPicker", StringComparison.Ordinal));
        Assert.DoesNotContain(global, i => i.Message.Contains("bypassPermissions", StringComparison.Ordinal));
        Assert.Contains(global, i => i.Message.Contains("확인 불가", StringComparison.Ordinal));
    }
}

public sealed class StepAReviewRegressionTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"product\":\"claude\",\"configDirName\":\".claude\",\"rules\":null}")]
    [InlineData("{\"schemaVersion\":1,\"product\":\"claude\",\"configDirName\":\".claude\",\"rules\":[{\"id\":\"x\",\"pattern\":null,\"role\":\"r\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"product\":\"claude\",\"configDirName\":\".claude\",\"rules\":[null]}")]
    public void Null_catalog_members_fall_back_to_the_built_in_copy(string json)
    {
        var dir = _temp.Combine("catalogs");
        Directory.CreateDirectory(Path.Combine(dir, "Claude"));
        File.WriteAllText(Path.Combine(dir, "Claude", "file-roles.json"), json);

        var loaded = DotDeck.Core.Catalogs.CatalogResources.Load(FileRoleCatalog.ClaudeResource, FileRoleCatalog.Parse, PhysicalFileStore.Instance, dir);

        Assert.Equal(DotDeck.Core.Catalogs.CatalogOrigin.BuiltIn, loaded.Origin);
        Assert.NotNull(loaded.Warning);
    }

    [Fact]
    public void Null_settings_key_groups_are_rejected()
    {
        Assert.Throws<InvalidDataException>(() => SettingsKeyCatalog.Parse("{\"schemaVersion\":1,\"groups\":[{\"keys\":null,\"message\":\"m\"}]}"));
    }

    [Fact]
    public void Invalid_path_characters_are_unknown_not_an_exception()
    {
        var context = new SettingsContext(ConfigScope.Project, @"C:\home", @"D:\proj");
        var check = HookScriptResolver.Check("python bad\0.py", null, context, _ => true);
        Assert.Equal(HookScriptStatus.Unknown, check.Status);
    }

    [Theory]
    [InlineData(@"""C:\Python\python.exe"" ""D:\proj\missing.py""")]
    [InlineData(@"C:\Python\python.exe D:\proj\missing.py")]
    [InlineData(@"/usr/bin/env python D:\proj\missing.py")]
    public void Interpreters_given_by_path_are_skipped(string command)
    {
        var check = HookScriptResolver.Check(command, null, null, _ => false);
        Assert.Equal(HookScriptStatus.Missing, check.Status);
        Assert.Equal(@"D:\proj\missing.py", check.Path);
    }

    [Fact]
    public void Failed_override_save_leaves_memory_unchanged()
    {
        var blocker = _temp.Combine("blocker");
        File.WriteAllText(blocker, "a file where the data folder should be");
        var store = new EditorOverrideStore(PhysicalFileStore.Instance, Path.Combine(blocker, "editor-overrides.json"));
        var target = _temp.Combine("x.json");

        Assert.ThrowsAny<IOException>(() => store.Set(target, EditorKind.ClaudeSettings));
        Assert.Null(store.Get(target));
    }
}
