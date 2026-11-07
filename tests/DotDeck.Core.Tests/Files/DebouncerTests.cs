using DotDeck.Core.Files;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.Files;

public sealed class DebouncerTests
{
    private static readonly TimeSpan s_delay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan s_max = TimeSpan.FromSeconds(2);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 7, 0, 0, 0, TimeSpan.Zero));
    private readonly List<(TimeSpan Due, Action Callback, Handle Handle)> _scheduled = [];
    private int _fired;

    private Debouncer Create() => new(s_delay, s_max, () => _fired++, _time, (due, callback) =>
    {
        var handle = new Handle();
        _scheduled.Add((due, callback, handle));
        return handle;
    });

    private void RunLatest() => _scheduled[^1].Callback();

    [Fact]
    public void Burst_fires_once_after_quiet_period()
    {
        var debouncer = Create();
        debouncer.Trigger();
        debouncer.Trigger();
        debouncer.Trigger();

        Assert.Equal(3, _scheduled.Count);
        Assert.True(_scheduled[0].Handle.Disposed);
        Assert.True(_scheduled[1].Handle.Disposed);

        // A replaced callback that still runs (timer race) does nothing.
        _scheduled[0].Callback();
        Assert.Equal(0, _fired);

        RunLatest();
        Assert.Equal(1, _fired);
    }

    [Fact]
    public void Constant_churn_still_fires_by_the_max_delay()
    {
        var debouncer = Create();
        debouncer.Trigger();
        _time.Advance(TimeSpan.FromMilliseconds(1900));
        debouncer.Trigger();

        Assert.Equal(TimeSpan.FromMilliseconds(100), _scheduled[^1].Due);

        _time.Advance(TimeSpan.FromSeconds(1));
        debouncer.Trigger();
        Assert.Equal(TimeSpan.Zero, _scheduled[^1].Due);
    }

    [Fact]
    public void New_burst_after_firing_starts_a_new_deadline()
    {
        var debouncer = Create();
        debouncer.Trigger();
        _time.Advance(TimeSpan.FromSeconds(5));
        RunLatest();

        debouncer.Trigger();
        Assert.Equal(s_delay, _scheduled[^1].Due);
    }

    [Fact]
    public void Dispose_cancels_pending_work()
    {
        var debouncer = Create();
        debouncer.Trigger();
        debouncer.Dispose();

        Assert.True(_scheduled[0].Handle.Disposed);
        RunLatest();
        debouncer.Trigger();
        Assert.Equal(0, _fired);
        Assert.Single(_scheduled);
    }

    [Fact]
    public void Real_watcher_reports_a_new_file()
    {
        using var temp = new TempDirectory();
        using var signal = new ManualResetEventSlim();
        using var watcher = new FolderWatcherFactory().Watch(temp.Path, signal.Set);
        Assert.NotNull(watcher);

        File.WriteAllText(temp.Combine("new.md"), "x");

        Assert.True(signal.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Missing_folder_is_not_watched()
    {
        using var temp = new TempDirectory();

        Assert.Null(new FolderWatcherFactory().Watch(temp.Combine("missing"), () => { }));
    }

    private sealed class Handle : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
