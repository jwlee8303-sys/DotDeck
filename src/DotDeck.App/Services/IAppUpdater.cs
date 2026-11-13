namespace DotDeck.App.Services;

/// <summary>A newer release found by <see cref="IAppUpdater.CheckAsync"/>. <paramref name="Handle"/> belongs to the updater.</summary>
public sealed record AppUpdateOffer(string CurrentVersion, string NewVersion, string? Notes, object Handle);

/// <summary>Checks, downloads and applies application updates (kept behind an interface so view models stay testable).</summary>
public interface IAppUpdater
{
    /// <summary>False for development runs and pre-1.5 ZIP builds (no Velopack Update.exe): those cannot update themselves.</summary>
    bool IsInstalled { get; }

    /// <summary>The newer release on the update channel, or null when this version is current.</summary>
    Task<AppUpdateOffer?> CheckAsync(CancellationToken cancellationToken);

    /// <summary>Downloads and verifies the release; <paramref name="progress"/> receives 0–100.</summary>
    Task DownloadAsync(AppUpdateOffer offer, Action<int> progress, CancellationToken cancellationToken);

    /// <summary>Starts the updater, which waits for this process to exit, applies the release and restarts the app.</summary>
    void ApplyOnExit(AppUpdateOffer offer);
}
