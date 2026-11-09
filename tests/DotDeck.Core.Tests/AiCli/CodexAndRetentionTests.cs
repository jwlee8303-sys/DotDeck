using System.Text;
using DotDeck.Core.AiCli.Launch;
using DotDeck.Core.AiCli.Sessions;
using DotDeck.Core.Files;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.AiCli;

public sealed class CodexJsonlTests
{
    [Fact]
    public void Tool_failures_report_status_and_safe_permission_reasons_without_command_output()
    {
        var events = CodexJsonlParser.Parse("""{"type":"item.completed","item":{"type":"command_execution","command":"apply_patch","status":"failed","exit_code":1,"aggregated_output":"sandbox access denied: private file content"}}""", false);
        var error = Assert.Single(events);
        Assert.True(error.IsError);
        Assert.Equal(AiOutputKind.ToolError, error.Kind);
        Assert.Contains("sandbox", error.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private file content", error.Text, StringComparison.Ordinal);
        Assert.True(Assert.Single(CodexJsonlParser.Parse("""{"type":"item.completed","item":{"type":"file_change","status":"failed"}}""", false)).IsError);
    }

    [Fact]
    public void Thread_id_is_taken_only_from_a_valid_thread_started_event()
    {
        const string id = "3f2a6c1e-1111-4222-8333-944455556666";
        Assert.Equal(id, CodexJsonlParser.ThreadId("{\"type\":\"thread.started\",\"thread_id\":\"" + id + "\"}"));
        Assert.Null(CodexJsonlParser.ThreadId("""{"type":"thread.started","thread_id":"-x"}"""));
        Assert.Null(CodexJsonlParser.ThreadId("""{"type":"item.completed","thread_id":"3f2a6c1e-1111-4222-8333-944455556666"}"""));
    }

    [Fact]
    public void Command_output_and_mcp_result_bodies_are_never_shown()
    {
        var command = CodexJsonlParser.Parse("""{"type":"item.completed","item":{"type":"command_execution","command":"git status","aggregated_output":"private file content"}}""", false);
        Assert.Equal("명령: git status", Assert.Single(command).Text);
        var mcp = CodexJsonlParser.Parse("""{"type":"item.completed","item":{"type":"mcp_tool_call","server":"dotdeck_session","tool":"find_files","result":{"content":"private file content"}}}""", false);
        Assert.Equal("MCP: dotdeck_session/find_files", Assert.Single(mcp).Text);
        var text = CodexJsonlParser.Parse("""{"type":"item.completed","item":{"type":"agent_message","text":"API_KEY=fake-secret"}}""", false);
        Assert.DoesNotContain("fake-secret", Assert.Single(text).Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("{\"type\":\"turn.failed\",\"error\":\"oops\"}")]
    [InlineData("{\"type\":{},\"item\":[]}")]
    [InlineData("{\"type\":\"item.completed\",\"item\":\"oops\"}")]
    public void Malformed_events_do_not_break_the_output_panel(string line) => CodexJsonlParser.Parse(line, false);
}

public sealed class CodexSandboxPolicyTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("[windows]\nsandbox = \"elevated\"", "elevated")]
    [InlineData("windows.sandbox = 'unelevated'", "unelevated")]
    [InlineData("[windows]\nsandbox = \"mxc\"", "mxc")]
    [InlineData("# windows.sandbox = 'elevated'\nmodel='test'", null)]
    [InlineData("[windows]\nsandbox = false", null)]
    [InlineData("[windows]\nsandbox = 'disabled'", null)]
    [InlineData("malformed = ", null)]
    public void Only_explicit_supported_windows_sandbox_configuration_is_accepted(string config, string? expected)
    {
        File.WriteAllText(_temp.Combine("config.toml"), config, new UTF8Encoding(false));
        Assert.Equal(expected, CodexSandboxPolicy.ReadMode(PhysicalFileStore.Instance, _temp.Path));
    }
}

public sealed class SessionRetentionTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly DateTimeOffset _now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    public void Dispose() => _temp.Dispose();

    private string Session(int daysOld, bool closed = true)
    {
        var dir = _temp.Combine(Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "backups"));
        File.WriteAllText(Path.Combine(dir, "backups", "fake.txt"), "fake backup");
        if (closed) SessionRetention.MarkClosed(dir, _now.AddDays(-daysOld));
        return dir;
    }

    [Fact]
    public void Unreviewed_running_and_recent_sessions_are_preserved()
    {
        var unreviewed = Session(30, closed: false);
        var leased = Session(30);
        var active = Session(30);
        var recent = Session(1);
        var old = Session(8);
        using var lease = new FileStream(Path.Combine(leased, SessionRetention.LeaseFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var deleted = SessionRetention.Prune(_temp.Path, _now, new HashSet<string> { Path.GetFileName(active) });
        Assert.Equal(old, Assert.Single(deleted));
        Assert.All(new[] { unreviewed, leased, active, recent }, d => Assert.True(Directory.Exists(d)));
    }

    [Fact]
    public void Count_limit_keeps_twenty_and_unknown_folders_are_untouched()
    {
        for (var i = 0; i < 23; i++) Session(i % 2);
        var unknown = _temp.Combine("unrelated");
        Directory.CreateDirectory(unknown);
        SessionRetention.MarkClosed(unknown, _now.AddDays(-30));
        Assert.Equal(3, SessionRetention.Prune(_temp.Path, _now).Count);
        Assert.Equal(21, Directory.GetDirectories(_temp.Path).Length);
        Assert.True(Directory.Exists(unknown));
    }
}
