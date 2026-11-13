using System.Net.Http;
using DotDeck.App.Services;
using DotDeck.App.ViewModels;
using DotDeck.Core.Logging;

namespace DotDeck.App.Tests;

public sealed class AppUpdateCoordinatorTests
{
    private static readonly AppUpdateOffer Offer = new("1.5.0", "1.6.0", "### Fixed\n- 수정", new object());

    private sealed class FakeUpdater : IAppUpdater
    {
        public bool IsInstalled { get; set; } = true;
        public Func<CancellationToken, Task<AppUpdateOffer?>> Check { get; set; } = _ => Task.FromResult<AppUpdateOffer?>(Offer);
        public Exception? DownloadError { get; set; }
        public Exception? ApplyError { get; set; }
        public int Checks { get; private set; }
        public int Downloads { get; private set; }
        public AppUpdateOffer? Applied { get; private set; }

        public Task<AppUpdateOffer?> CheckAsync(CancellationToken cancellationToken)
        {
            Checks++;
            return Check(cancellationToken);
        }

        public Task DownloadAsync(AppUpdateOffer offer, Action<int> progress, CancellationToken cancellationToken)
        {
            Downloads++;
            if (DownloadError is not null) return Task.FromException(DownloadError);
            progress(50);
            progress(100);
            return Task.CompletedTask;
        }

        public void ApplyOnExit(AppUpdateOffer offer)
        {
            if (ApplyError is not null) throw ApplyError;
            Applied = offer;
        }
    }

    private sealed class FakeUi : IUpdateUi
    {
        public bool Accept { get; set; } = true;
        public bool ConfirmExit { get; set; } = true;
        public List<AppUpdateOffer> Asked { get; } = [];
        public List<string> Infos { get; } = [];
        public List<string> Errors { get; } = [];
        public List<string> Statuses { get; } = [];
        public int ExitRequests { get; private set; }

        public bool AskInstall(AppUpdateOffer offer)
        {
            Asked.Add(offer);
            return Accept;
        }

        public void ShowInfo(string message) => Infos.Add(message);
        public void ShowError(string message) => Errors.Add(message);
        public void SetStatus(string message) => Statuses.Add(message);
        public Task<bool> ConfirmExitAsync() => Task.FromResult(ConfirmExit);
        public void RequestExit() => ExitRequests++;
    }

    private sealed class NullLog : IAppLog
    {
        public event EventHandler<AppLogEntry>? EntryWritten { add { } remove { } }
        public void Write(AppLogLevel level, string message, Exception? exception = null) { }
    }

    private readonly FakeUpdater _updater = new();
    private readonly FakeUi _ui = new();

    private AppUpdateCoordinator Create(TimeSpan? timeout = null) => new(_updater, _ui, new NullLog(), timeout);

    [Fact]
    public async Task Accepting_downloads_then_applies_on_exit_and_requests_exit()
    {
        await Create().CheckAsync(UpdateCheckTrigger.Startup);

        Assert.Single(_ui.Asked);
        Assert.Equal(1, _updater.Downloads);
        Assert.Same(Offer, _updater.Applied);
        Assert.Equal(1, _ui.ExitRequests);
        Assert.Contains(_ui.Statuses, s => s.Contains("100%"));
    }

    [Fact]
    public async Task Later_does_not_download_and_keeps_running()
    {
        _ui.Accept = false;
        await Create().CheckAsync(UpdateCheckTrigger.Startup);

        Assert.Equal(0, _updater.Downloads);
        Assert.Null(_updater.Applied);
        Assert.Equal(0, _ui.ExitRequests);
    }

    [Fact]
    public async Task Cancelled_exit_keeps_the_download_for_next_start_without_applying_now()
    {
        _ui.ConfirmExit = false;
        await Create().CheckAsync(UpdateCheckTrigger.Manual);

        Assert.Equal(1, _updater.Downloads);
        Assert.Null(_updater.Applied);
        Assert.Equal(0, _ui.ExitRequests);
        Assert.Contains("다음에", _ui.Statuses[^1]);
    }

    [Fact]
    public async Task Startup_check_is_silent_when_current_offline_or_not_installed()
    {
        _updater.Check = _ => Task.FromResult<AppUpdateOffer?>(null);
        await Create().CheckAsync(UpdateCheckTrigger.Startup);

        _updater.Check = _ => Task.FromException<AppUpdateOffer?>(new HttpRequestException("offline"));
        await Create().CheckAsync(UpdateCheckTrigger.Startup);

        _updater.IsInstalled = false;
        await Create().CheckAsync(UpdateCheckTrigger.Startup);

        Assert.Empty(_ui.Infos);
        Assert.Empty(_ui.Errors);
        Assert.Empty(_ui.Asked);
        Assert.Equal(2, _updater.Checks); // Not-installed builds never query the feed.
    }

    [Fact]
    public async Task Manual_check_reports_current_failure_and_not_installed()
    {
        _updater.Check = _ => Task.FromResult<AppUpdateOffer?>(null);
        await Create().CheckAsync(UpdateCheckTrigger.Manual);
        Assert.Contains("최신 버전", Assert.Single(_ui.Infos));

        _updater.Check = _ => Task.FromException<AppUpdateOffer?>(new HttpRequestException("offline"));
        await Create().CheckAsync(UpdateCheckTrigger.Manual);
        Assert.Single(_ui.Errors);

        _updater.IsInstalled = false;
        await Create().CheckAsync(UpdateCheckTrigger.Manual);
        Assert.Equal(2, _ui.Infos.Count);
    }

    [Fact]
    public async Task A_check_that_hangs_times_out_quietly()
    {
        _updater.Check = async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Offer;
        };

        await Create(TimeSpan.FromMilliseconds(50)).CheckAsync(UpdateCheckTrigger.Startup);

        Assert.Empty(_ui.Asked);
        Assert.Empty(_ui.Errors);
    }

    [Fact]
    public async Task Download_failure_reports_and_a_retry_works()
    {
        var coordinator = Create();
        _updater.DownloadError = new HttpRequestException("broken");
        await coordinator.CheckAsync(UpdateCheckTrigger.Manual);
        Assert.Single(_ui.Errors);
        Assert.Null(_updater.Applied);
        Assert.False(coordinator.IsBusy);

        _updater.DownloadError = null;
        await coordinator.CheckAsync(UpdateCheckTrigger.Manual);
        Assert.Same(Offer, _updater.Applied);
        Assert.Equal(1, _ui.ExitRequests);
    }

    [Fact]
    public async Task Apply_failure_does_not_exit()
    {
        _updater.ApplyError = new InvalidOperationException("updater missing");
        await Create().CheckAsync(UpdateCheckTrigger.Manual);

        Assert.Single(_ui.Errors);
        Assert.Equal(0, _ui.ExitRequests);
    }

    [Fact]
    public async Task A_second_check_while_one_runs_is_ignored()
    {
        var gate = new TaskCompletionSource<AppUpdateOffer?>();
        _updater.Check = _ => gate.Task;
        var coordinator = Create();

        var first = coordinator.CheckAsync(UpdateCheckTrigger.Startup);
        await coordinator.CheckAsync(UpdateCheckTrigger.Manual);
        gate.SetResult(null);
        await first;

        Assert.Equal(1, _updater.Checks);
        Assert.Contains(_ui.Statuses, s => s.Contains("이미"));
    }

    [Fact]
    public void Message_shows_both_versions_and_truncates_long_notes()
    {
        var message = ShellViewModel.FormatUpdateMessage(Offer with { Notes = new string('가', 5000) });

        Assert.Contains("현재 버전: 1.5.0", message);
        Assert.Contains("새 버전: 1.6.0", message);
        Assert.True(message.Length < 1700);
        Assert.EndsWith("…", message);
    }
}
