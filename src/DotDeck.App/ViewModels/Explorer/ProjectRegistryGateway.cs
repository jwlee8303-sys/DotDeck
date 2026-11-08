using DotDeck.Core.Projects;

namespace DotDeck.App.ViewModels.Explorer;

/// <summary>
/// Runs project registry changes one at a time on the thread pool, so the UI never waits for the
/// cross-process lock or disk I/O. Reads (<see cref="ProjectRegistry.Projects"/>, Find) stay on the UI thread:
/// the registry publishes a new immutable list on every successful change, so a reader sees either the
/// old or the new list. <see cref="ProjectRegistry.Changed"/> is raised on a pool thread.
/// </summary>
public sealed class ProjectRegistryGateway
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ProjectRegistryGateway(ProjectRegistry registry)
    {
        Registry = registry;
    }

    public ProjectRegistry Registry { get; }

    /// <summary>Callers resume on their own context; the gate itself is released on the pool (see <see cref="TryRunNow"/>).</summary>
    public async Task<T> RunAsync<T>(Func<ProjectRegistry, T> change) => await RunCoreAsync(change);

    public Task RunAsync(Action<ProjectRegistry> change) => RunAsync(r =>
    {
        change(r);
        return true;
    });

    /// <summary>
    /// Shutdown only: runs <paramref name="change"/> on the calling thread once earlier changes finished. False when
    /// the gate did not free up within <paramref name="timeout"/> (the change is then not made).
    /// </summary>
    public bool TryRunNow(Action<ProjectRegistry> change, TimeSpan timeout)
    {
        if (!_gate.Wait(timeout))
        {
            return false;
        }

        try
        {
            change(Registry);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ConfigureAwait(false): the release must not need the UI thread, which TryRunNow may be blocking.
    private async Task<T> RunCoreAsync<T>(Func<ProjectRegistry, T> change)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() => change(Registry)).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
