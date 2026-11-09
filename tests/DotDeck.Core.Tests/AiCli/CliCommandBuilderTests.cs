using System.Text.Json.Nodes;
using DotDeck.Core.AiCli.Launch;
using DotDeck.Core.AiCli.Scope;
using DotDeck.Core.Files;
using DotDeck.Core.Paths;
using DotDeck.Core.Projects;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.AiCli;

public sealed class WindowsCommandLineTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "\"\"")]
    [InlineData("has space", "\"has space\"")]
    [InlineData(@"D:\web game\", "\"D:\\web game\\\\\"")]
    [InlineData(@"C:\path\", @"C:\path\")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"a\\""b", "\"a\\\\\\\\\\\"b\"")]
    [InlineData("line1\nline2", "\"line1\nline2\"")]
    [InlineData("탭\t한글", "\"탭\t한글\"")]
    [InlineData("100%&^|<>", "100%&^|<>")]
    [InlineData("it's", "it's")]
    public void Quote_follows_msvc_rules(string argument, string expected) =>
        Assert.Equal(expected, WindowsCommandLine.Quote(argument));

    [Fact]
    public void Join_always_quotes_the_executable()
    {
        Assert.Equal("\"C:\\Program Files\\x.exe\" a \"b c\"", WindowsCommandLine.Join(@"C:\Program Files\x.exe", ["a", "b c"]));
        Assert.Throws<ArgumentException>(() => WindowsCommandLine.Join("a\"b.exe", []));
    }

    [Theory]
    [InlineData("never", "\"never\"")]
    [InlineData("C:\\a \"b\"\n", "\"C:\\\\a \\\"b\\\"\\n\"")]
    [InlineData("tab\tcr\r", "\"tab\\tcr\\r\"")]
    [InlineData("\u0001", "\"\\u0001\"")]
    [InlineData("한글 'quote'", "\"한글 'quote'\"")]
    public void Toml_basic_string_escapes(string value, string expected) =>
        Assert.Equal(expected, TomlString.Basic(value));
}

public sealed class CliCommandBuilderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly SessionScope _scope;
    private readonly SessionScope _single;
    private readonly string _primary;
    private readonly string _web;
    private readonly string _special;

    public CliCommandBuilderTests()
    {
        _primary = _temp.Combine("DotDeck");
        _web = _temp.Combine("웹 게임");
        _special = _temp.Combine("a&b %x% 'q'");
        foreach (var dir in new[] { _primary, _web, _special })
        {
            Directory.CreateDirectory(dir);
        }

        var paths = new AppPaths(_temp.Combine("home"), HomeSource.CommandLine, _temp.Combine("r"), _temp.Combine("l"));
        ProjectDefinition[] projects =
        [
            new() { Id = "a", Kind = ProjectKind.Folder, Name = "DotDeck", Root = _primary },
            new() { Id = "b", Kind = ProjectKind.Folder, Name = "웹게임", Root = _web },
            new() { Id = "c", Kind = ProjectKind.Folder, Name = "Special", Root = _special },
        ];
        _scope = SessionScope.Create("a", ["b", "c"], projects, paths, PhysicalFileStore.Instance).Scope!;
        _single = SessionScope.Create("a", [], projects, paths, PhysicalFileStore.Instance).Scope!;
    }

    public void Dispose() => _temp.Dispose();

    private static readonly CliLocation Claude = new(CliKind.Claude, @"C:\Users\me\.local\bin\claude.exe", []);
    private static readonly CliLocation Codex = new(CliKind.Codex, @"C:\Program Files\nodejs\node.exe", [@"C:\Users\me\AppData\Roaming\npm\node_modules\@openai\codex\bin\codex.js"]);
    private const string SessionDir = @"C:\Users\me\AppData\Local\DotDeck\sessions\s 1";

    private CliLaunchRequest Request(CliLocation cli, LaunchMode mode, SessionScope? scope = null) => new()
    {
        Cli = cli,
        Mode = mode,
        Scope = scope ?? _scope,
        SessionDirectory = SessionDir,
        Context = "DotDeck 세션 범위:\n| DotDeck | D:\\x |\n\"quoted\"",
        ConversationId = "3f2a6c1e-1111-4222-8333-944455556666",
        Prompt = "-README 만들어줘",
    };

    private static int IndexAfter(IReadOnlyList<string> args, string option) => args.ToList().IndexOf(option) + 1;

    [Fact]
    public void Claude_interactive_command_line()
    {
        var plan = CliCommandBuilder.Build(Request(Claude, LaunchMode.Interactive));

        Assert.Equal(Claude.Executable, plan.Executable);
        Assert.Equal(_primary, plan.WorkingDirectory);
        Assert.Equal(
            [
                "--add-dir", _web, "--add-dir", _special,
                "--settings", Path.Combine(SessionDir, "settings.json"),
                "--session-id", "3f2a6c1e-1111-4222-8333-944455556666",
                "-n", "Claude · DotDeck (+웹게임, Special)",
                "--append-system-prompt", "DotDeck 세션 범위:\n| DotDeck | D:\\x |\n\"quoted\"",
            ],
            plan.Arguments);
        Assert.Null(plan.StandardInput);
        Assert.Contains(plan.Arguments.Count - 1, plan.FreeTextArguments);
        Assert.DoesNotContain("-p", plan.Arguments);
    }

    [Fact]
    public void Claude_without_additional_projects_has_no_add_dir()
    {
        var plan = CliCommandBuilder.Build(Request(Claude, LaunchMode.Interactive, _single));

        Assert.DoesNotContain("--add-dir", plan.Arguments);
        Assert.Equal("Claude · DotDeck", plan.Arguments[IndexAfter(plan.Arguments, "-n")]);
    }

    [Fact]
    public void Claude_non_interactive_restricts_tools_and_sends_prompt_on_stdin()
    {
        var plan = CliCommandBuilder.Build(Request(Claude, LaunchMode.NonInteractive));
        var args = plan.Arguments;

        Assert.Equal("-p", args[0]);
        Assert.Equal("stream-json", args[IndexAfter(args, "--output-format")]);
        Assert.Contains("--verbose", args);
        Assert.Equal("dontAsk", args[IndexAfter(args, "--permission-mode")]);
        Assert.Equal("none", args[IndexAfter(args, "--permission-prompts")]);
        Assert.Equal("Read,Glob,Grep,Edit,Write,NotebookEdit", args[IndexAfter(args, "--tools")]);
        Assert.Equal("Read,Glob,Grep,Edit,Write,NotebookEdit", args[IndexAfter(args, "--allowedTools")]);
        Assert.Contains("--strict-mcp-config", args);
        Assert.DoesNotContain("Bash", string.Join(' ', args), StringComparison.Ordinal);
        Assert.DoesNotContain("-n", args);
        Assert.Equal("-README 만들어줘", plan.StandardInput);
        Assert.DoesNotContain("-README 만들어줘", args);
    }

    [Fact]
    public void Read_only_shell_option_adds_only_git_reads()
    {
        var plan = CliCommandBuilder.Build(Request(Claude, LaunchMode.NonInteractive) with { AllowReadOnlyShell = true, MaxBudgetUsd = 1.5m });

        Assert.Equal("Read,Glob,Grep,Edit,Write,NotebookEdit,Bash", plan.Arguments[IndexAfter(plan.Arguments, "--tools")]);
        Assert.EndsWith("Bash(git status),Bash(git diff *),Bash(git log *)", plan.Arguments[IndexAfter(plan.Arguments, "--allowedTools")], StringComparison.Ordinal);
        Assert.Equal("1.5", plan.Arguments[IndexAfter(plan.Arguments, "--max-budget-usd")]);
    }

    [Fact]
    public void Resume_uses_resume_and_fresh_system_prompt()
    {
        var plan = CliCommandBuilder.Build(Request(Claude, LaunchMode.Interactive) with { Resume = true });

        Assert.Equal("3f2a6c1e-1111-4222-8333-944455556666", plan.Arguments[IndexAfter(plan.Arguments, "--resume")]);
        Assert.Equal("off", plan.Arguments[IndexAfter(plan.Arguments, "--system-prompt-snapshot")]);
        Assert.DoesNotContain("--session-id", plan.Arguments);
    }

    [Fact]
    public void Conversation_id_must_be_a_uuid()
    {
        Assert.Throws<ArgumentException>(() => CliCommandBuilder.Build(Request(Claude, LaunchMode.Interactive) with { ConversationId = "--dangerously-skip-permissions" }));
    }

    [Fact]
    public void Mcp_config_is_passed_and_allowed_in_5c()
    {
        var plan = CliCommandBuilder.Build(Request(Claude, LaunchMode.NonInteractive) with { McpConfigPath = @"C:\s\mcp.json" });

        Assert.Equal(@"C:\s\mcp.json", plan.Arguments[IndexAfter(plan.Arguments, "--mcp-config")]);
        Assert.EndsWith(",mcp__dotdeck_session__*", plan.Arguments[IndexAfter(plan.Arguments, "--allowedTools")], StringComparison.Ordinal);
    }

    [Fact]
    public void Codex_non_interactive_command_line()
    {
        var plan = CliCommandBuilder.Build(Request(Codex, LaunchMode.NonInteractive) with { ConversationId = null });
        var args = plan.Arguments;

        Assert.Equal(Codex.Executable, plan.Executable);
        Assert.Equal(Codex.PrefixArguments[0], args[0]);
        Assert.Equal("exec", args[1]);
        Assert.Equal(_primary, args[IndexAfter(args, "-C")]);
        Assert.Equal(["--add-dir", _web, "--add-dir", _special], args.Skip(IndexAfter(args, "-C") + 1).Take(4));
        Assert.Equal("workspace-write", args[IndexAfter(args, "-s")]);
        Assert.Contains("approval_policy=\"never\"", args);
        Assert.Contains("developer_instructions=\"DotDeck 세션 범위:\\n| DotDeck | D:\\\\x |\\n\\\"quoted\\\"\"", args);
        Assert.Equal("-", args[^1]);
        Assert.Equal(Path.Combine(SessionDir, "last-message.txt"), args[IndexAfter(args, "-o")]);
        Assert.Contains("--json", args);
        Assert.DoesNotContain("-a", args);
        Assert.Equal("-README 만들어줘", plan.StandardInput);
    }

    [Fact]
    public void Codex_approves_only_the_five_scoped_session_tools_and_pins_all_selected_roots()
    {
        var plan = CliCommandBuilder.Build(Request(Codex, LaunchMode.NonInteractive) with
        {
            McpBridgeExecutable = @"C:\Program Files\DotDeck\DotDeck.exe",
        });
        var config = Assert.Single(plan.Arguments, a => a.StartsWith("mcp_servers.dotdeck_session=", StringComparison.Ordinal));
        var parsed = DotDeck.Core.Toml.TomlEditDocument.Parse(System.Text.Encoding.UTF8.GetBytes(config));
        Assert.Contains("required=true", config, StringComparison.Ordinal);
        Assert.Contains("default_tools_approval_mode=\"prompt\"", config, StringComparison.Ordinal);
        foreach (var name in DotDeck.Core.AiCli.Mcp.McpServer.Names)
            Assert.Contains(parsed.Entries, e => e.Path.SequenceEqual(new[] { "mcp_servers", "dotdeck_session", "tools", name, "approval_mode" }) && e.Literal == "\"approve\"");
        Assert.Equal(5, parsed.Entries.Count(e => e.Path.LastOrDefault() == "approval_mode"));
        Assert.Contains("sandbox_workspace_write.writable_roots=[" + string.Join(",", _scope.Roots.Select(TomlString.Basic)) + "]", plan.Arguments);
        Assert.Contains("approval_policy=\"never\"", plan.Arguments);
    }

    [Fact]
    public void Codex_interactive_command_line()
    {
        var plan = CliCommandBuilder.Build(Request(Codex, LaunchMode.Interactive) with { ConversationId = null });

        Assert.DoesNotContain("exec", plan.Arguments);
        Assert.Equal("on-request", plan.Arguments[IndexAfter(plan.Arguments, "-a")]);
        Assert.Contains("--no-daemon", plan.Arguments);
        Assert.Null(plan.StandardInput);
    }

    [Fact]
    public void Codex_resume_keeps_scope_and_json_output_in_non_interactive_mode()
    {
        var plan = CliCommandBuilder.Build(Request(Codex, LaunchMode.NonInteractive) with { Resume = true });
        Assert.Contains("resume", plan.Arguments);
        Assert.Equal("3f2a6c1e-1111-4222-8333-944455556666", plan.Arguments[IndexAfter(plan.Arguments, "resume")]);
        Assert.Contains("workspace-write", plan.Arguments);
        Assert.Contains("--add-dir", plan.Arguments);
        Assert.Contains("--json", plan.Arguments);
        Assert.Equal("-", plan.Arguments[^1]);
        Assert.Throws<NotSupportedException>(() => CliCommandBuilder.Build(Request(Codex, LaunchMode.Interactive) with { Resume = true }));
    }

    [Theory]
    [InlineData(CliKind.Claude, LaunchMode.Interactive)]
    [InlineData(CliKind.Claude, LaunchMode.NonInteractive)]
    [InlineData(CliKind.Codex, LaunchMode.Interactive)]
    [InlineData(CliKind.Codex, LaunchMode.NonInteractive)]
    public void No_forbidden_option_is_ever_built(CliKind cli, LaunchMode mode)
    {
        var request = Request(cli == CliKind.Claude ? Claude : Codex, mode) with
        {
            ConversationId = cli == CliKind.Claude ? "3f2a6c1e-1111-4222-8333-944455556666" : null,
            AllowReadOnlyShell = true,
            Context = "mention --dangerously-skip-permissions and bypassPermissions in free text",
        };

        var plan = CliCommandBuilder.Build(request);

        for (var i = 0; i < plan.Arguments.Count; i++)
        {
            if (plan.FreeTextArguments.Contains(i))
            {
                continue;
            }

            foreach (var forbidden in ForbiddenOptions.All)
            {
                Assert.DoesNotContain(forbidden, plan.Arguments[i], StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Forbidden_option_in_a_plan_is_rejected()
    {
        var plan = new CliLaunchPlan { Executable = @"C:\c.exe", Arguments = ["--permission-mode", "bypassPermissions"], WorkingDirectory = @"C:\" };
        Assert.Throws<InvalidOperationException>(() => ForbiddenOptions.Validate(plan));
        Assert.Throws<InvalidOperationException>(() => ForbiddenOptions.Validate(plan with { Arguments = ["-c", "sandbox_mode=\"danger-full-access\""] }));
        Assert.Throws<InvalidOperationException>(() => ForbiddenOptions.Validate(plan with { Arguments = ["--dangerously-skip-permissions"] }));
    }

    [Fact]
    public void Sandbox_homes_are_passed_only_when_given()
    {
        var real = CliCommandBuilder.Build(Request(Claude, LaunchMode.Interactive));
        Assert.Empty(real.Environment);

        var sandbox = CliCommandBuilder.Build(Request(Claude, LaunchMode.Interactive) with
        {
            SandboxClaudeConfigDir = @"C:\sb\.claude",
            SandboxCodexHome = @"C:\sb\.codex",
        });
        Assert.Equal(@"C:\sb\.claude", sandbox.Environment["CLAUDE_CONFIG_DIR"]);
        Assert.Equal(@"C:\sb\.codex", sandbox.Environment["CODEX_HOME"]);
    }

    [Fact]
    public void Command_line_quotes_paths_with_spaces_and_special_characters()
    {
        var line = CliCommandBuilder.Build(Request(Claude, LaunchMode.Interactive)).CommandLine;

        Assert.StartsWith("\"C:\\Users\\me\\.local\\bin\\claude.exe\" --add-dir \"", line, StringComparison.Ordinal);
        Assert.Contains($"\"{_web}\"", line, StringComparison.Ordinal);
        Assert.Contains($"\"{_special}\"", line, StringComparison.Ordinal);
    }
}

public sealed class CliLocatorTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeProcessEnvironment _env = new();

    public void Dispose() => _temp.Dispose();

    private string Touch(params string[] parts) => Write(string.Empty, parts);

    private string Write(string content, params string[] parts)
    {
        var path = _temp.Combine(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    // The shape of the shim npm writes for @openai/codex.
    private const string CodexShim = "@ECHO off\r\nGOTO start\r\n:start\r\nSETLOCAL\r\nCALL :find_dp0\r\n\"%_prog%\"  \"%dp0%\\node_modules\\@openai\\codex\\bin\\codex.js\" %*\r\n";

    private CliLocatorResult Find(CliKind cli, string? overridePath = null) =>
        new CliLocator(_env, PhysicalFileStore.Instance).Find(cli, overridePath);

    [Fact]
    public void Finds_exe_on_path()
    {
        var exe = Touch("bin", "claude.exe");
        _env.Variables["PATH"] = $"relative;{_temp.Combine("empty")};\"{_temp.Combine("bin")}\"";

        var result = Find(CliKind.Claude);

        Assert.Equal(exe, result.Location!.Executable, ignoreCase: true);
        Assert.Empty(result.Location.PrefixArguments);
    }

    [Fact]
    public void Relative_path_entries_are_never_searched()
    {
        Touch("claude.exe");
        _env.Variables["PATH"] = ".;bin";

        Assert.Null(Find(CliKind.Claude).Location);
    }

    [Fact]
    public void Npm_shim_is_replaced_by_node_and_script()
    {
        Write(CodexShim, "npm", "codex.cmd");
        var script = Touch("npm", "node_modules", "@openai", "codex", "bin", "codex.js");
        var node = Touch("node", "node.exe");
        _env.Variables["PATH"] = $"{_temp.Combine("npm")};{_temp.Combine("node")}";
        _env.Variables["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";

        var result = Find(CliKind.Codex);

        Assert.Equal(node, result.Location!.Executable);
        Assert.Equal([script], result.Location.PrefixArguments);
    }

    [Fact]
    public void Batch_file_without_package_script_is_refused()
    {
        Touch("npm", "codex.cmd");
        _env.Variables["PATH"] = _temp.Combine("npm");

        var result = Find(CliKind.Codex);

        Assert.Null(result.Location);
        Assert.Contains("배치 파일", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Batch_file_that_does_not_run_the_package_script_is_refused()
    {
        Write("@echo off\r\nnode \"%~dp0\\evil.js\" %*\r\n", "npm", "codex.cmd");
        Touch("npm", "node_modules", "@openai", "codex", "bin", "codex.js");
        Touch("node", "node.exe");
        _env.Variables["PATH"] = $"{_temp.Combine("npm")};{_temp.Combine("node")}";

        var result = Find(CliKind.Codex);

        Assert.Null(result.Location);
        Assert.Contains("배치 파일", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Override_must_be_an_existing_absolute_exe()
    {
        var exe = Touch("custom", "claude.exe");
        var cmd = Touch("custom", "claude.cmd");

        Assert.Equal(exe, Find(CliKind.Claude, exe).Location!.Executable);
        Assert.Null(Find(CliKind.Claude, cmd).Location);
        Assert.Null(Find(CliKind.Claude, "claude.exe").Location);
        Assert.Null(Find(CliKind.Claude, _temp.Combine("missing.exe")).Location);
    }
}

public sealed class SessionFilesWriterTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Settings_inject_guard_heartbeat_and_native_boundaries()
    {
        var dir = _temp.Combine("sessions", "s 1");
        var exe = @"C:\Program Files\DotDeck\DotDeck.exe";

        SessionFilesWriter.Write(exe, dir, new ScopeDocument
        {
            SessionId = "s 1",
            Roots = [new ScopeFileRoot { Path = @"C:\work" }, new ScopeFileRoot { Path = @"D:\웹 게임" }, new ScopeFileRoot { Path = @"\\srv\share" }],
        });

        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")))!;
        var guard = settings["hooks"]!["PreToolUse"]![0]!;
        Assert.Equal("*", (string?)guard["matcher"]);
        Assert.Equal(exe, (string?)guard["hooks"]![0]!["command"]);
        Assert.Equal(["--dotdeck-guard", Path.Combine(dir, "scope.json")], guard["hooks"]![0]!["args"]!.AsArray().Select(n => (string)n!));
        Assert.Equal("block", (string?)guard["hooks"]![0]!["onFailure"]);
        var heartbeat = settings["hooks"]!["SessionStart"]![0]!["hooks"]![0]!;
        Assert.Equal(["--dotdeck-heartbeat", dir], heartbeat["args"]!.AsArray().Select(n => (string)n!));
        Assert.True((bool)settings["permissions"]!["blockWritesOutsideWorkingDirectories"]!);
        Assert.True((bool)settings["permissions"]!["blockReadsOutsideWorkingDirectories"]!);
        var deny = settings["permissions"]!["deny"]!.AsArray().Select(n => (string)n!).ToList();
        Assert.Contains("Read(**/auth.json)", deny);
        Assert.Contains("Edit(**/*.pem)", deny);
        Assert.Contains("Read(**/.env*)", deny);
        // Relative rules do not reach additional directories: each representable root gets absolute rules too.
        Assert.Contains("Read(//c/work/**/.env*)", deny);
        Assert.Contains("Edit(//d/웹 게임/**/id_rsa*)", deny);
        Assert.DoesNotContain(deny, d => d.Contains("srv", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("s 1", ScopeFile.Read(Path.Combine(dir, "scope.json")).SessionId);
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    [Fact]
    public void Outside_reads_option_drops_the_read_boundary()
    {
        var json = JsonNode.Parse(SessionFilesWriter.BuildSettings(@"C:\d.exe", @"C:\s", Scope with { AllowOutsideReads = true }))!;

        Assert.Null(json["permissions"]!["blockReadsOutsideWorkingDirectories"]);
        Assert.True((bool)json["permissions"]!["blockWritesOutsideWorkingDirectories"]!);
    }

    [Fact]
    public void Relative_paths_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => SessionFilesWriter.BuildSettings("DotDeck.exe", @"C:\s", Scope));
    }

    private static readonly ScopeDocument Scope = new() { SessionId = "s", Roots = [new ScopeFileRoot { Path = @"C:\work" }] };

    [Theory]
    [InlineData(@"C:\", "//c/**/")]
    [InlineData(@"C:\a b\c", "//c/a b/c/**/")]
    [InlineData(@"\\srv\share", null)]
    [InlineData(@"C:\a(1)", null)]
    [InlineData(@"C:\[x]", null)]
    public void Root_rule_paths(string root, string? expected) =>
        Assert.Equal(expected, SessionFilesWriter.ToRulePath(root));

    [Theory]
    [InlineData(".env.staging.local")]
    [InlineData(".envrc")]
    [InlineData(".env.custom")]
    [InlineData("server.pem")]
    [InlineData("id_ed25519.pub")]
    [InlineData("auth.json")]
    public void Every_secret_name_is_covered_by_a_deny_pattern(string name)
    {
        Assert.True(DotDeck.Core.Explorer.SecretFilePolicy.IsSecret(name));
        Assert.Contains(SessionFilesWriter.SecretNamePatterns, p => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: true));
    }
}
