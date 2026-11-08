namespace DotDeck.Core.Files;

/// <summary>
/// Runs an action once a burst of triggers has been quiet for <c>delay</c>, but no later than <c>maxDelay</c>
/// after the first trigger of the burst (so constant churn still refreshes). Thread-safe; the action runs on
/// the scheduler's thread (thread pool by default).
/// </summary>
public sealed class Debouncer : IDisposable
{
    private readonly TimeSpan _delay;
    private readonly TimeSpan _maxDelay;
    private readonly Action _action;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private readonly Lock _gate = new();
    private IDisposable? _pending;
    private DateTimeOffset? _burstStart;
    private int _generation;
    private bool _disposed;

    /// <param name="schedule">Runs a callback after a delay; the returned handle cancels it. Default: a timer.</param>
    public Debouncer(TimeSpan delay, TimeSpan maxDelay, Action action, TimeProvider? time = null, Func<TimeSpan, Action, IDisposable>? schedule = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        _delay = delay;
        _maxDelay = maxDelay < delay ? delay : maxDelay;
        _action = action;
        _time = time ?? TimeProvider.System;
        _schedule = schedule ?? ScheduleOnTimer;
    }

    public void Trigger()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var now = _time.GetUtcNow();
            _burstStart ??= now;

            // Never postpone past the burst's deadline.
            var due = _delay;
            var remaining = _burstStart.Value + _maxDelay - now;
            if (remaining < due)
            {
                due = remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
            }

            _pending?.Dispose();
            var generation = ++_generation;
            _pending = _schedule(due, () => Fire(generation));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _pending?.Dispose();
            _pending = null;
        }
    }

    private void Fire(int generation)
    {
        lock (_gate)
        {
            // A later trigger replaced this callback (its cancel may race with the timer).
            if (_disposed || generation != _generation)
            {
                return;
            }

            _pending?.Dispose();
            _pending = null;
            _burstStart = null;
        }

        _action();
    }

    private static IDisposable ScheduleOnTimer(TimeSpan due, Action callback) =>
        new Timer(_ => callback(), null, due, Timeout.InfiniteTimeSpan);
}
