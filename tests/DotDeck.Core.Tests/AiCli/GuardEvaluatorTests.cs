using System.Text.Json.Nodes;
using DotDeck.Core.AiCli.Guard;

namespace DotDeck.Core.Tests.AiCli;

public sealed class GuardEvaluatorTests
{
    private const string Root = @"C:\work\proj";
    private const string Other = @"D:\web game";

    private static readonly GuardPolicy Policy = new()
    {
        Roots = [Root, Other],
        ReadOnlyRoots = [@"C:\Users\me\.claude\skills"],
        Home = @"C:\Users\me",
    };

    private static GuardEvaluator Evaluator(FakeFinalPathResolver? resolver = null) =>
        new(resolver ?? new FakeFinalPathResolver().Add(Root, Other, @"C:\work\proj2", @"C:\Users\me\.claude\skills", @"C:\tmp\scratch"));

    private static string Input(string tool, object toolInput, string cwd = Root, string? scratchpad = null)
    {
        var obj = new JsonObject
        {
            ["session_id"] = "s1",
            ["hook_event_name"] = "PreToolUse",
            ["cwd"] = cwd,
            ["tool_name"] = tool,
            ["tool_input"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(toolInput)),
        };
        if (scratchpad is not null)
        {
            obj["scratchpad_dir"] = scratchpad;
        }

        return obj.ToJsonString();
    }

    private static GuardVerdict Verdict(string json, GuardPolicy? policy = null, FakeFinalPathResolver? resolver = null) =>
        Evaluator(resolver).EvaluateJson(json, policy ?? Policy).Verdict;

    [Theory]
    [InlineData("Write")]
    [InlineData("Edit")]
    [InlineData("MultiEdit")]
    [InlineData("Read")]
    public void File_tools_inside_scope_get_no_decision(string tool)
    {
        Assert.Equal(GuardVerdict.None, Verdict(Input(tool, new { file_path = @"C:\work\proj\src\a.cs" })));
        Assert.Equal(GuardVerdict.None, Verdict(Input(tool, new { file_path = @"D:\web game\README.md" })));
    }

    [Theory]
    [InlineData("Write")]
    [InlineData("Edit")]
    [InlineData("Read")]
    public void File_tools_outside_scope_are_denied(string tool)
    {
        Assert.Equal(GuardVerdict.Deny, Verdict(Input(tool, new { file_path = @"C:\work\proj2\a.cs" })));
        Assert.Equal(GuardVerdict.Deny, Verdict(Input(tool, new { file_path = @"C:\work\proj\..\proj2\a.cs" })));
    }

    [Fact]
    public void Notebook_edit_uses_notebook_path()
    {
        Assert.Equal(GuardVerdict.None, Verdict(Input("NotebookEdit", new { notebook_path = @"C:\work\proj\n.ipynb" })));
        Assert.Equal(GuardVerdict.Deny, Verdict(Input("NotebookEdit", new { notebook_path = @"C:\work\proj2\n.ipynb" })));
    }

    [Fact]
    public void Missing_file_path_is_denied()
    {
        Assert.Equal(GuardVerdict.Deny, Verdict(Input("Write", new { content = "x" })));
    }

    [Theory]
    [InlineData(@"C:\work\proj\.env")]
    [InlineData(@"C:\work\proj\config\id_rsa")]
    [InlineData(@"C:\work\proj\cert.pem")]
    [InlineData(@"D:\web game\auth.json")]
    public void Secret_files_inside_scope_are_denied_for_reads_and_writes(string path)
    {
        Assert.Equal(GuardVerdict.Deny, Verdict(Input("Read", new { file_path = path })));
        Assert.Equal(GuardVerdict.Deny, Verdict(Input("Edit", new { file_path = path })));
    }

    [Fact]
    public void Env_example_is_not_secret()
    {
        Assert.Equal(GuardVerdict.None, Verdict(Input("Read", new { file_path = @"C:\work\proj\.env.example" })));
    }

    [Fact]
    public void Link_alias_to_secret_file_is_denied()
    {
        var resolver = new FakeFinalPathResolver().Add(Root, Other).Link(@"C:\work\proj\notes.txt", @"C:\work\proj\.env");

        Assert.Equal(GuardVerdict.Deny, Verdict(Input("Read", new { file_path = @"C:\work\proj\notes.txt" }), resolver: resolver));
    }

    [Fact]
    public void Read_only_root_allows_read_but_denies_write()
    {
        const string skill = @"C:\Users\me\.claude\skills\x\SKILL.md";

        Assert.Equal(GuardVerdict.None, Verdict(Input("Read", new { file_path = skill })));
        Assert.Equal(GuardVerdict.Deny, Verdict(Input("Write", new { file_path = skill })));
    }

    [Fact]
    public void Scratchpad_from_hook_input_is_writable()
    {
        var json = Input("Write", new { file_path = @"C:\tmp\scratch\notes.md" }, scratchpad: @"C:\tmp\scratch");

        Assert.Equal(GuardVerdict.None, Verdict(json));
        Assert.Equal(GuardVerdict.Deny, Verdict(Input("Write", new { file_path = @"C:\tmp\scratch\notes.md" })));
    }

    [Fact]
    public void Outside_reads_can_be_allowed_but_never_secrets_or_writes()
    {
        var policy = Policy with { AllowOutsideReads = true };

        Assert.Equal(GuardVerdict.None, Verdict(Input("Read", new { file_path = @"C:\work\proj2\a.cs" }), policy));
        Assert.Equal(GuardVerdict.Deny, Verdict(Input("Read", new { file_path = @"C:\work\proj2\.env" }), policy));
        Assert.Equal(GuardVerdict.Deny, Verdict(Input("Write", new { file_path = @"C:\work\proj2\a.cs" }), policy));
    }

    [Fact]
    public void Glob_checks_path_and_absolute_pattern_prefix()
    {
        Assert.Equal(GuardVerdict.None, Verdict(Input("Glob", new { pattern = "**/*.md" })));
        Assert.Equal(GuardVerdict.None, Verdict(Input("Glob", new { pattern = "*.md", path = @"D:\web game" })));
        Assert.Equal(GuardVerdict.Deny, Verdict(Input("Glob", new { pattern = "*.md", path = @"C:\work\proj2" })));
        Assert.Equal(GuardVerdict.Deny, Verdict(Input("Glob", new { pattern = @"C:\work\proj2\**\*.md" })));
        Assert.Equal(GuardVerdict.Deny, Verdict(Input("Glob", new { pattern = "../proj2/**/*.md" })));
    }

    [Theory]
    [InlineData("**/.env")]
    [InlineData("**/*.pem")]
    [InlineData(".env*")]
    public void Glob_for_secret_names_is_denied(string pattern)
    {
        Assert.Equal(GuardVerdict.Deny, Verdict(Input("Glob", new { pattern })));
    }

    [Fact]
    public void Grep_checks_path()
    {
        Assert.Equal(GuardVerdict.None, Verdict(Input("Grep", new { pattern = "TODO" })));
        Assert.Equal(GuardVerdict.None, Verdict(Input("Grep", new { pattern = "TODO", path = @"C:\work\proj\src" })));
        Assert.Equal(GuardVerdict.Deny, Verdict(Input("Grep", new { pattern = "TODO", path = @"C:\Users\me" })));
        Assert.Equal(GuardVerdict.Deny, Verdict(Input("Grep", new { pattern = "KEY", path = @"C:\work\proj\.env" })));
    }

    [Theory]
    [InlineData("git status")]
    [InlineData("dotnet build DotDeck.slnx")]
    [InlineData(@"cat src/a.cs > /c/work/proj/out.txt")]
    [InlineData("ls -la ./src")]
    [InlineData("echo hi > NUL")]
    [InlineData(@"type ""D:\web game\README.md""")]
    [InlineData("grep -r 'a/b' src")]
    public void Shell_commands_inside_scope_get_no_decision(string command)
    {
        Assert.Equal(GuardVerdict.None, Verdict(Input("Bash", new { command })));
    }

    [Theory]
    [InlineData(@"echo x > 'C:\work\proj2\a.txt'")]
    [InlineData(@"echo x >> ../proj2/a.txt")]
    [InlineData(@"rm -rf /c/work/proj2")]
    [InlineData(@"cp a.txt ""C:\Users\me\Desktop\a.txt""")]
    [InlineData(@"git -C D:/other commit -m x")]
    [InlineData(@"cat ~/secrets.txt")]
    [InlineData(@"mv a.txt --target-directory=C:/elsewhere")]
    [InlineData(@"echo x 2> C:/work/proj2/err.log")]
    [InlineData("cd .. && rm a.txt")]
    [InlineData("ls && cd /c/work")]
    public void Shell_commands_with_outside_targets_are_denied(string command)
    {
        Assert.Equal(GuardVerdict.Deny, Verdict(Input("Bash", new { command })));
    }

    [Theory]
    [InlineData("cd src && rm a.txt")]
    [InlineData("rm $HOME/a.txt")]
    [InlineData("rm $(pwd)/a")]
    [InlineData("echo `whoami`")]
    [InlineData("python -c \"open('x','w')\"")]
    [InlineData(@"./build.ps1")]
    [InlineData("curl -o out.bin https://example.com/x")]
    [InlineData("bash -c 'rm x'")]
    [InlineData("rm /tmp/a")]
    [InlineData("echo %USERPROFILE%")]
    [InlineData("ls && cd /c/work/proj/src")]
    public void Shell_commands_with_unknowable_targets_are_asked(string command)
    {
        Assert.Equal(GuardVerdict.Ask, Verdict(Input("Bash", new { command })));
    }

    [Theory]
    [InlineData(@"Set-Content -Path C:\work\proj2\a.txt -Value x", GuardVerdict.Deny)]
    [InlineData(@"Get-Content .\src\a.cs", GuardVerdict.None)]
    [InlineData(@"Remove-Item -LiteralPath 'D:\web game\old.txt'", GuardVerdict.None)]
    [InlineData(@"Remove-Item $env:TEMP\x", GuardVerdict.Ask)]
    [InlineData(@"powershell -EncodedCommand AAAA", GuardVerdict.Ask)]
    [InlineData(@"& 'C:\work\proj\tool.exe'", GuardVerdict.Ask)]
    [InlineData(@"iex (Get-Content x.ps1 -Raw)", GuardVerdict.Ask)]
    [InlineData(@"Copy-Item a.txt -Destination:C:\elsewhere\a.txt", GuardVerdict.Deny)]
    public void PowerShell_commands_are_analyzed(string command, GuardVerdict expected)
    {
        Assert.Equal(expected, Verdict(Input("PowerShell", new { command })));
    }

    [Theory]
    [InlineData("cat .env")]
    [InlineData(@"type config/id_rsa")]
    [InlineData("Get-Content auth.json")]
    public void Shell_access_to_secret_files_is_denied(string command)
    {
        Assert.Equal(GuardVerdict.Deny, Verdict(Input("Bash", new { command })));
    }

    [Fact]
    public void Bash_backslash_escapes_are_resolved_like_bash()
    {
        // ".en\v" is ".env" for Bash.
        Assert.Equal(GuardVerdict.Deny, Verdict(Input("Bash", new { command = @"cat .en\v" })));
        // Unquoted backslashes vanish in Bash ("D:\x\a" becomes the drive-relative "D:xa"): unknowable on another drive,
        // and on the cwd drive it really is a file in cwd.
        Assert.Equal(GuardVerdict.Ask, Verdict(Input("Bash", new { command = @"echo x > D:\x\a.txt" })));
        Assert.Equal(GuardVerdict.None, Verdict(Input("Bash", new { command = @"echo x > C:\work\proj2\a.txt" })));
        // In PowerShell a backslash is an ordinary path separator.
        Assert.Equal(GuardVerdict.Deny, Verdict(Input("PowerShell", new { command = @"Set-Content C:\work\proj2\a.txt x" })));
    }

    [Fact]
    public void Line_breaks_start_a_new_command()
    {
        Assert.Equal(GuardVerdict.Ask, Verdict(Input("Bash", new { command = "echo ok\ncd src\nrm a.txt" })));
        Assert.Equal(GuardVerdict.Ask, Verdict(Input("PowerShell", new { command = "Write-Output ok\r\nSet-Location src" })));
    }

    [Fact]
    public void Plain_shell_names_are_resolved_through_links()
    {
        var resolver = new FakeFinalPathResolver()
            .Add(Root, Other, @"C:\elsewhere")
            .Link(@"C:\work\proj\notes.txt", @"C:\elsewhere\notes.txt")
            .Link(@"C:\work\proj\cfg", @"C:\work\proj\.env");

        Assert.Equal(GuardVerdict.Deny, Verdict(Input("Bash", new { command = "cat notes.txt" }), resolver: resolver));
        Assert.Equal(GuardVerdict.Deny, Verdict(Input("Bash", new { command = "cat cfg" }), resolver: resolver));
        Assert.Equal(GuardVerdict.None, Verdict(Input("Bash", new { command = "git commit -m \"fix con bug\"" }), resolver: resolver));
    }

    [Fact]
    public void Relative_search_path_anchors_the_pattern_prefix()
    {
        var resolver = new FakeFinalPathResolver().Add(@"C:\work\sub", @"C:\other");
        var policy = Policy with { Roots = [@"C:\work"] };
        var json = Input("Glob", new { path = "..", pattern = "../**/*.md" }, cwd: @"C:\work\sub");

        Assert.Equal(GuardVerdict.Deny, Verdict(json, policy, resolver));
        Assert.Equal(GuardVerdict.None, Verdict(Input("Glob", new { path = "..", pattern = "**/*.md" }, cwd: @"C:\work\sub"), policy, resolver));
    }

    [Theory]
    [InlineData("auth.*")]
    [InlineData("*.{pem,key}")]
    [InlineData("**/.env*")]
    [InlineData("id_*")]
    [InlineData("foo*.pem")]
    [InlineData(".env.staging*")]
    [InlineData("deploy?.key")]
    public void Grep_glob_aimed_at_secret_files_is_denied(string glob)
    {
        Assert.Equal(GuardVerdict.Deny, Verdict(Input("Grep", new { pattern = ".", glob })));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("*.json")]
    [InlineData("**/*.{cs,md}")]
    [InlineData("*.env.example")]
    public void Broad_or_ordinary_globs_are_allowed(string glob)
    {
        Assert.Equal(GuardVerdict.None, Verdict(Input("Grep", new { pattern = ".", glob })));
    }

    [Theory]
    [InlineData("cat ./auth.*")]
    [InlineData("cat sub/.env*")]
    [InlineData("type C:/work/proj/keys/foo*.pem")]
    public void Shell_paths_with_secret_globs_are_denied(string command)
    {
        Assert.Equal(GuardVerdict.Deny, Verdict(Input("Bash", new { command })));
    }

    [Fact]
    public void Deny_wins_over_ask_in_one_command()
    {
        Assert.Equal(GuardVerdict.Deny, Verdict(Input("Bash", new { command = @"cd x; echo y > C:/work/proj2/a" })));
    }

    [Fact]
    public void Own_mcp_tools_pass_and_foreign_mcp_tools_are_asked()
    {
        Assert.Equal(GuardVerdict.None, Verdict(Input("mcp__dotdeck_session__find_files", new { project = "x", pattern = "*.md" })));
        Assert.Equal(GuardVerdict.Ask, Verdict(Input("mcp__filesystem__write_file", new { path = @"C:\work\proj\a" })));
    }

    [Theory]
    [InlineData("WebFetch")]
    [InlineData("TodoWrite")]
    [InlineData("Agent")]
    public void Tools_without_paths_get_no_decision(string tool)
    {
        Assert.Equal(GuardVerdict.None, Verdict(Input(tool, new { prompt = @"look at C:\Windows", url = "https://x" })));
    }

    [Fact]
    public void Unknown_tools_have_path_like_fields_checked()
    {
        Assert.Equal(GuardVerdict.Deny, Verdict(Input("FutureTool", new { target_path = @"C:\work\proj2\a" })));
        Assert.Equal(GuardVerdict.None, Verdict(Input("FutureTool", new { target_path = @"C:\work\proj\a", note = @"C:\x" })));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"tool_name\":\"\"}")]
    public void Malformed_input_is_denied(string json)
    {
        Assert.Equal(GuardVerdict.Deny, Evaluator().EvaluateJson(json, Policy).Verdict);
    }

    [Fact]
    public void Relative_cwd_cannot_widen_relative_paths()
    {
        Assert.Equal(GuardVerdict.Deny, Verdict(Input("Write", new { file_path = "a.txt" }, cwd: "relative")));
    }

    [Fact]
    public void Hook_output_json_matches_the_pre_tool_use_schema()
    {
        var deny = GuardDecision.Deny("범위 밖: D:\\x");
        var json = JsonNode.Parse(deny.ToHookOutputJson()!)!;

        Assert.Equal("PreToolUse", (string?)json["hookSpecificOutput"]!["hookEventName"]);
        Assert.Equal("deny", (string?)json["hookSpecificOutput"]!["permissionDecision"]);
        Assert.Equal("범위 밖: D:\\x", (string?)json["hookSpecificOutput"]!["permissionDecisionReason"]);
        Assert.Contains("범위", deny.ToHookOutputJson(), StringComparison.Ordinal); // Hangul is not escaped.
        Assert.Equal("ask", (string?)JsonNode.Parse(GuardDecision.Ask("x").ToHookOutputJson()!)!["hookSpecificOutput"]!["permissionDecision"]);
        Assert.Null(GuardDecision.NoDecision.ToHookOutputJson());
    }

    [Fact]
    public void Guard_never_emits_allow()
    {
        foreach (var verdict in Enum.GetValues<GuardVerdict>())
        {
            var json = new GuardDecision(verdict, "r").ToHookOutputJson();
            Assert.DoesNotContain("\"allow\"", json ?? string.Empty, StringComparison.Ordinal);
        }
    }
}
