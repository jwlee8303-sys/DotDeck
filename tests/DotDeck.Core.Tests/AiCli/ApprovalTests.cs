using DotDeck.Core.AiCli.Approvals;
using DotDeck.Core.AiCli.Launch;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.AiCli;

public sealed class ApprovalTests
{
    private readonly ApprovalClock _time = new();
    private static readonly ScreenPromptCatalog Catalog = ScreenPromptCatalog.BuiltIn();

    [Theory]
    [InlineData(CliKind.Claude, "Do you want to proceed?", "1. Yes", "2. No", "2.1.330 (Claude Code)", "1")]
    [InlineData(CliKind.Codex, "Would you like to run the following command?", "1. Yes, proceed (y)", "3. No (esc)", "codex-cli 0.171.0", "y")]
    [InlineData(CliKind.Antigravity, "Allow creation of this file?", "1. Yes, allow creation", "2. No, deny creation", "1.5.15", "1")]
    public void ScreenPromptRequiresBothOptionsAndVerifiedVersion(CliKind cli, string question, string yes, string no, string versionOutput, string allow)
    {
        var provider = new ScreenApprovalProvider(cli, Catalog);
        var screen = $"sample.txt\r\n{question}\r\n{yes}\r\n{no}";
        var match = Assert.IsType<ScreenPromptMatch>(provider.Match(screen));
        Assert.Equal(question, match.Question);
        Assert.Equal("sample.txt", match.Context);
        Assert.Null(provider.Match($"{question}\n{yes}"));
        Assert.Null(provider.Match(screen + new string('\n', ScreenApprovalProvider.SearchLines)));
        foreach (var other in Catalog.Prompts.Where(prompt => prompt.Cli != cli))
        {
            Assert.Null(new ScreenApprovalProvider(other.Cli, Catalog).Match(screen));
        }

        var version = ScreenApprovalProvider.ParseVersion(versionOutput);
        Assert.Equal(match.Definition.VerifiedVersion, version);
        Assert.True(ScreenApprovalProvider.CanAnswer(match.Definition, version));
        Assert.Equal(allow, ScreenApprovalProvider.KeysFor(match, ApprovalDecision.Allow, version));
        Assert.Equal("\u001b", ScreenApprovalProvider.KeysFor(match, ApprovalDecision.Deny, version));
        Assert.Null(ScreenApprovalProvider.KeysFor(match, ApprovalDecision.Allow, "0.0.0"));
        Assert.Null(ScreenApprovalProvider.KeysFor(match, ApprovalDecision.Deny, null));
        Assert.False(ScreenApprovalProvider.CanAnswer(match.Definition with { DenyKeys = null }, version));
        var probe = provider.ToRequest(match, "session", null, version, _time.GetUtcNow(), live: false);
        Assert.False(probe.CanRespond);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("version unavailable")]
    [InlineData("codex-cli 0.171")]
    public void ParseVersionReturnsNullWithoutThreeComponents(string? output) =>
        Assert.Null(ScreenApprovalProvider.ParseVersion(output));

    [Fact]
    public void PrereleaseDoesNotEnableKeysForVerifiedStableVersion()
    {
        var provider = new ScreenApprovalProvider(CliKind.Codex, Catalog);
        var match = Assert.IsType<ScreenPromptMatch>(provider.Match(
            "Would you like to run the following command?\n1. Yes, proceed (y)\n3. No (esc)"));
        var version = ScreenApprovalProvider.ParseVersion("codex-cli 0.171.0-beta.1");

        Assert.Null(ScreenApprovalProvider.KeysFor(match, ApprovalDecision.Allow, version));
    }

    [Fact]
    public void ScreenPromptOptionsAboveQuestionDoNotFormAnApprovalPrompt()
    {
        var provider = new ScreenApprovalProvider(CliKind.Claude, Catalog);

        Assert.Null(provider.Match("1. Yes\n2. No\nOrdinary output\nDo you want to proceed?"));
    }

    [Fact]
    public void ScreenPromptOptionsOnTheQuestionLineDoNotFormAnApprovalPrompt()
    {
        var provider = new ScreenApprovalProvider(CliKind.Claude, Catalog);

        Assert.Null(provider.Match("Ordinary output\nDo you want to proceed? 1. Yes 2. No"));
    }

    [Theory]
    [InlineData(ApprovalDecision.Allow, ApprovalState.Allowed)]
    [InlineData(ApprovalDecision.Deny, ApprovalState.Denied)]
    public async Task UserDecisionFinishesRequestAndRejectsFurtherAnswers(ApprovalDecision decision, ApprovalState state)
    {
        using var service = new ApprovalService(new(), _time);
        var request = HookRequest();
        var channel = new RecordingChannel();
        service.Add(request, channel);
        Assert.Same(request, Assert.Single(service.Waiting));

        var cancellation = TestContext.Current.CancellationToken;
        Assert.True(await service.RespondAsync(request.Id, decision, ApprovalResponder.User, cancellation));
        Assert.False(await service.RespondAsync(request.Id, ApprovalDecision.Allow, ApprovalResponder.User, cancellation));
        Assert.Equal(state, request.State);
        Assert.Equal(_time.GetUtcNow(), request.ResolvedAt);
        Assert.Empty(service.Waiting);
        Assert.Same(request, service.LastFor(request.SessionKey));
        Assert.Equal(decision, Assert.Single(channel.Decisions));
        Assert.Equal(0, channel.Releases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandedOffOrDisconnectedHookDoesNotSendAnApproval(bool disconnect)
    {
        using var service = new ApprovalService(new(), _time);
        var request = HookRequest();
        var channel = new RecordingChannel();
        service.Add(request, channel);

        if (disconnect)
        {
            Assert.True(service.HookEnded(request.Id));
            Assert.Equal(ApprovalState.AnsweredInTerminal, request.State);
        }
        else
        {
            Assert.True(service.HandOff(request.Id));
            Assert.Equal(ApprovalState.HandedToTerminal, request.State);
            Assert.True(service.MarkToolRan(request.SessionKey, "sample-tool-call"));
            Assert.Equal(ApprovalState.AnsweredInTerminal, request.State);
        }

        Assert.False(await service.RespondAsync(request.Id, ApprovalDecision.Allow, ApprovalResponder.User, TestContext.Current.CancellationToken));
        Assert.Empty(channel.Decisions);
        Assert.True(channel.Releases > 0);
        Assert.Empty(service.Waiting);
    }

    [Fact]
    public async Task TimedOutChannelExpiresRequest()
    {
        using var service = new ApprovalService(new(), _time);
        var request = HookRequest();
        var channel = new RecordingChannel(() => Task.FromException<bool>(new TimeoutException()));
        service.Add(request, channel);

        Assert.False(await service.RespondAsync(request.Id, ApprovalDecision.Deny, ApprovalResponder.User, TestContext.Current.CancellationToken));
        Assert.Equal(ApprovalState.Expired, request.State);
        Assert.Equal(ApprovalDecision.Deny, Assert.Single(channel.Decisions));
        Assert.Equal(1, channel.Releases);
    }

    [Fact]
    public void DisposingServiceReleasesWaitingHooksWithoutDecisions()
    {
        using var service = new ApprovalService(new(), _time);
        var channel = new RecordingChannel();
        service.Add(HookRequest(), channel);

        service.Dispose();
        _time.Advance(TimeSpan.FromMinutes(1));
        service.Tick();
        Assert.Empty(service.Waiting);
        Assert.Empty(channel.Decisions);
        Assert.Equal(1, channel.Releases);
        Assert.Null(ClaudeApprovalHook.HookOutput("none"));
    }

    [Fact]
    public void TurningDetectionOffHandsOffThePendingHookWithoutSendingADecision()
    {
        using var service = new ApprovalService(new(), _time);
        var request = HookRequest();
        var channel = new RecordingChannel();
        service.Add(request, channel);

        service.ApplySettings(new ApprovalSettings { Enabled = false });
        _time.Advance(TimeSpan.FromMinutes(1));
        service.Tick();
        Assert.Equal(ApprovalState.HandedToTerminal, request.State);
        Assert.Empty(channel.Decisions);
        Assert.Equal(1, channel.Releases);
    }

    [Fact]
    public void HookRequestIsHandedToTheTerminalAtTheWaitLimit()
    {
        using var service = new ApprovalService(new(), _time);
        var request = HookRequest();
        var channel = new RecordingChannel();
        service.Add(request, channel);
        Assert.Equal(request.RequestedAt + ApprovalSettings.HookWaitLimit, request.WaitLimit);

        _time.Advance(ApprovalSettings.HookWaitLimit - TimeSpan.FromSeconds(1));
        service.Tick();
        Assert.Equal(ApprovalState.Pending, request.State);

        _time.Advance(TimeSpan.FromSeconds(1));
        service.Tick();
        Assert.Equal(ApprovalState.Expired, request.State);
        Assert.Empty(channel.Decisions);
        Assert.Equal(1, channel.Releases);
        Assert.True(ClaudeApprovalHook.HookTimeoutSeconds > ApprovalSettings.HookWaitLimit.TotalSeconds);
    }

    [Fact]
    public async Task ConcurrentAnswersSendOnlyOneDecision()
    {
        var completeSend = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new ApprovalService(new(), _time);
        var request = HookRequest();
        var channel = new RecordingChannel(() => completeSend.Task);
        service.Add(request, channel);
        var cancellation = TestContext.Current.CancellationToken;

        var first = service.RespondAsync(request.Id, ApprovalDecision.Allow, ApprovalResponder.User, cancellation);
        Assert.False(await service.RespondAsync(request.Id, ApprovalDecision.Deny, ApprovalResponder.User, cancellation));
        completeSend.SetResult(true);

        Assert.True(await first);
        Assert.Equal(ApprovalDecision.Allow, Assert.Single(channel.Decisions));
        Assert.Equal(ApprovalState.Allowed, request.State);
    }

    private ApprovalRequest HookRequest(string command = "preview sample.txt") =>
        new("session", CliKind.Claude, ApprovalSource.Hook, "Bash", command, null, _time.GetUtcNow())
        {
            CanRespond = true,
            MatchKey = "sample-tool-call",
        };

    private sealed class ApprovalClock : TimeProvider
    {
        private readonly FakeTimeProvider _clock = new(DateTimeOffset.UnixEpoch);

        public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow();

        public void Advance(TimeSpan elapsed) => _clock.Advance(elapsed);

        // Tick is driven directly by each test; suppress callbacks from the real thread pool.
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            System.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private sealed class RecordingChannel(Func<Task<bool>>? send = null) : IApprovalChannel
    {
        public List<ApprovalDecision> Decisions { get; } = [];

        public int Releases { get; private set; }

        public Task<bool> SendAsync(ApprovalDecision decision, CancellationToken cancellationToken)
        {
            Decisions.Add(decision);
            return send?.Invoke() ?? Task.FromResult(true);
        }

        public void Release() => Releases++;
    }
}
