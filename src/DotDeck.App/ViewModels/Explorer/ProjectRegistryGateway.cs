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

    public async Task<T> RunAsync<T>(Func<ProjectRegistry, T> change)
    {
        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            return await Task.Run(() => change(Registry)).ConfigureAwait(true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task RunAsync(Action<ProjectRegistry> change) => RunAsync(r =>
    {
        change(r);
        return true;
    });
}
