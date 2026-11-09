using System.Text;
using System.Text.Json.Nodes;
using DotDeck.Core.AiCli.Guard;
using DotDeck.Core.AiCli.Scope;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.AiCli;

public sealed class ScopeFileTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static ScopeDocument Doc(params string[] roots) => new()
    {
        SessionId = "s-1",
        Generation = 3,
        UpdatedAt = new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.FromHours(9)),
        Roots = [.. roots.Select(r => new ScopeFileRoot { Path = r, Name = "웹 게임", ProjectId = "p" })],
        ReadOnlyRoots = [@"C:\Users\me\.claude\skills"],
        Home = @"C:\Users\me",
    };

    [Fact]
    public void Write_then_read_round_trips()
    {
        var path = _temp.Combine("sessions", "s-1", "scope.json");
        var doc = Doc(@"D:\web game", @"C:\work\proj");

        ScopeFile.Write(path, doc);
        var read = ScopeFile.Read(path, "s-1");

        Assert.Equal(doc.SessionId, read.SessionId);
        Assert.Equal(3, read.Generation);
        Assert.Equal([@"D:\web game", @"C:\work\proj"], read.Roots.Select(r => r.Path));
        Assert.Equal(doc.ReadOnlyRoots, read.ReadOnlyRoots);
        Assert.Contains("웹 게임", File.ReadAllText(path), StringComparison.Ordinal); // Hangul not escaped.
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public void Rewrite_replaces_previous_scope()
    {
        var path = _temp.Combine("scope.json");
        ScopeFile.Write(path, Doc(@"C:\a", @"C:\b"));

        ScopeFile.Write(path, Doc(@"C:\a") with { Generation = 4 });

        var read = ScopeFile.Read(path);
        Assert.Equal(4, read.Generation);
        Assert.Equal([@"C:\a"], read.Roots.Select(r => r.Path));
    }

    [Fact]
    public async Task Read_survives_concurrent_rewrites()
    {
        var path = _temp.Combine("scope.json");
        ScopeFile.Write(path, Doc(@"C:\a"));
        using var stop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            var g = 0;
            while (!stop.IsCancellationRequested)
            {
                ScopeFile.Write(path, Doc(@"C:\a") with { Generation = ++g });
            }
        }, TestContext.Current.CancellationToken);

        for (var i = 0; i < 200; i++)
        {
            Assert.Equal("s-1", ScopeFile.Read(path).SessionId);
        }

        stop.Cancel();
        await writer;
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{\"schemaVersion\":1}")]
    [InlineData("{\"schemaVersion\":2,\"sessionId\":\"s-1\"}")]
    [InlineData("{\"schemaVersion\":1,\"sessionId\":\"s-1\",\"roots\":[{\"path\":\"relative\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"sessionId\":\"s-1\",\"roots\":[null]}")]
    [InlineData("{\"schemaVersion\":1,\"sessionId\":\"s-1\",\"readOnlyRoots\":[\"\"]}")]
    public void Invalid_contents_are_rejected(string json)
    {
        Assert.Throws<ScopeFileException>(() => ScopeFile.Parse(json));
    }

    [Fact]
    public void Session_mismatch_is_rejected()
    {
        Assert.Throws<ScopeFileException>(() => ScopeFile.Parse(ScopeFile.Serialize(Doc(@"C:\a")), "other"));
    }

    [Fact]
    public void Missing_file_is_rejected()
    {
        Assert.Throws<ScopeFileException>(() => ScopeFile.Read(_temp.Combine("missing", "scope.json")));
        Assert.Throws<ScopeFileException>(() => ScopeFile.Read(_temp.Combine("scope.json")));
    }
}

public sealed class GuardHostTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _root;
    private readonly string _scopePath;

    public GuardHostTests()
    {
        _root = _temp.Combine("proj");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_temp.Combine("other"));
        _scopePath = _temp.Combine("session", "scope.json");
        ScopeFile.Write(_scopePath, new ScopeDocument { SessionId = "s-1", Roots = [new ScopeFileRoot { Path = _root }] });
    }

    public void Dispose() => _temp.Dispose();

    private (int Exit, string Stdout, string Stderr) Run(string stdin, params string[] args)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(stdin));
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        var exit = GuardHost.RunGuard(args.Length == 0 ? [GuardHost.GuardSwitch, _scopePath] : args, input, output, error, LexicalPathResolver.Instance);
        return (exit, Encoding.UTF8.GetString(output.ToArray()), Encoding.UTF8.GetString(error.ToArray()));
    }

    private string Input(string file) => new JsonObject
    {
        ["tool_name"] = "Write",
        ["cwd"] = _root,
        ["tool_input"] = new JsonObject { ["file_path"] = file },
    }.ToJsonString();

    private static string? Decision(string stdout) =>
        stdout.Length == 0 ? null : (string?)JsonNode.Parse(stdout)!["hookSpecificOutput"]!["permissionDecision"];

    [Fact]
    public void Inside_scope_prints_nothing_and_exits_zero()
    {
        var (exit, stdout, _) = Run(Input(Path.Combine(_root, "a.txt")));

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
    }

    [Fact]
    public void Outside_scope_prints_deny_and_exits_zero()
    {
        var (exit, stdout, _) = Run(Input(_temp.Combine("other", "a.txt")));

        Assert.Equal(0, exit);
        Assert.Equal("deny", Decision(stdout));
    }

    [Fact]
    public void Narrowed_scope_applies_to_the_next_call()
    {
        var target = _temp.Combine("other", "a.txt");
        ScopeFile.Write(_scopePath, new ScopeDocument { SessionId = "s-1", Roots = [new ScopeFileRoot { Path = _root }, new ScopeFileRoot { Path = _temp.Combine("other") }] });
        Assert.Equal(string.Empty, Run(Input(target)).Stdout);

        ScopeFile.Write(_scopePath, new ScopeDocument { SessionId = "s-1", Generation = 1, Roots = [new ScopeFileRoot { Path = _root }] });

        Assert.Equal("deny", Decision(Run(Input(target)).Stdout));
    }

    [Fact]
    public void Missing_scope_file_fails_closed_with_exit_two()
    {
        File.Delete(_scopePath);

        var (exit, stdout, stderr) = Run(Input(Path.Combine(_root, "a.txt")));

        Assert.Equal(2, exit);
        Assert.Equal("deny", Decision(stdout));
        Assert.Contains("scope.json", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Bad_arguments_fail_closed()
    {
        Assert.Equal(2, Run("{}", GuardHost.GuardSwitch).Exit);
        Assert.Equal(2, Run("{}", GuardHost.GuardSwitch, "relative.json").Exit);
        Assert.Equal(2, Run("{}", "--other", _scopePath).Exit);
    }

    [Fact]
    public void Malformed_or_non_utf8_input_is_denied()
    {
        Assert.Equal("deny", Decision(Run("not json").Stdout));

        using var input = new MemoryStream([0x7B, 0xFF, 0xFE, 0x7D]);
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        var exit = GuardHost.RunGuard([GuardHost.GuardSwitch, _scopePath], input, output, error, LexicalPathResolver.Instance);
        Assert.Equal(2, exit);
        Assert.Equal("deny", Decision(Encoding.UTF8.GetString(output.ToArray())));
    }

    [Fact]
    public void Output_is_utf8_without_bom()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(Input(_temp.Combine("other", "한글.txt"))));
        using var output = new MemoryStream();
        using var error = new MemoryStream();

        GuardHost.RunGuard([GuardHost.GuardSwitch, _scopePath], input, output, error, LexicalPathResolver.Instance);

        var bytes = output.ToArray();
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Contains("한글", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void Error_messages_never_echo_the_hook_input()
    {
        File.Delete(_scopePath);
        var input = Input(Path.Combine(_root, "a.txt")).Replace("a.txt", "SECRET-VALUE-123", StringComparison.Ordinal);

        var (_, stdout, stderr) = Run(input);

        Assert.DoesNotContain("SECRET-VALUE-123", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-VALUE-123", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Heartbeat_writes_timestamp_and_never_fails()
    {
        var dir = _temp.Combine("session");
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 9, 1, 2, 3, TimeSpan.Zero));
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("{}"));

        Assert.Equal(0, GuardHost.RunHeartbeat([GuardHost.HeartbeatSwitch, dir], input, time));
        Assert.StartsWith("2026-11-09T01:02:03", File.ReadAllText(Path.Combine(dir, GuardHost.HeartbeatFileName)), StringComparison.Ordinal);
        Assert.Equal(0, GuardHost.RunHeartbeat([GuardHost.HeartbeatSwitch, _temp.Combine("missing")], Stream.Null, time));
        Assert.Equal(0, GuardHost.RunHeartbeat(["x"], Stream.Null, time));
    }
}
