using System.IO;
using DotDeck.App.Services;
using Velopack;
using Velopack.Sources;

namespace DotDeck.App.Infrastructure.Windows;

/// <summary>
/// Updates from the public GitHub Releases feed (stable channel, no token). <see cref="FeedVariable"/> may point at a
/// local release folder instead, for testing installs against a feed that users never see.
/// </summary>
public sealed class VelopackAppUpdater : IAppUpdater
{
    public const string RepositoryUrl = "https://github.com/jwlee8303-sys/DotDeck";
    public const string FeedVariable = "DOTDECK_UPDATE_FEED";

    private readonly UpdateManager _manager;

    public VelopackAppUpdater(string? feedOverride)
    {
        IUpdateSource source = string.IsNullOrWhiteSpace(feedOverride)
            ? new GithubSource(RepositoryUrl, accessToken: null, prerelease: false)
            : new SimpleFileSource(new DirectoryInfo(feedOverride));
        _manager = new UpdateManager(source);
    }

    public bool IsInstalled => _manager.IsInstalled;

    public async Task<AppUpdateOffer?> CheckAsync(CancellationToken cancellationToken)
    {
        var info = await _manager.CheckForUpdatesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        if (info is null || info.IsDowngrade)
        {
            return null;
        }

        return new AppUpdateOffer(
            _manager.CurrentVersion?.ToString() ?? "",
            info.TargetFullRelease.Version.ToString(),
            info.TargetFullRelease.NotesMarkdown,
            info);
    }

    public Task DownloadAsync(AppUpdateOffer offer, Action<int> progress, CancellationToken cancellationToken) =>
        _manager.DownloadUpdatesAsync(Unwrap(offer), progress, cancellationToken);

    public void ApplyOnExit(AppUpdateOffer offer) =>
        // Restart with the same options (--home/--data), so a sandbox run stays a sandbox run.
        _manager.WaitExitThenApplyUpdates(Unwrap(offer).TargetFullRelease, silent: false, restart: true,
            restartArgs: Environment.GetCommandLineArgs().Skip(1).ToArray());

    private static UpdateInfo Unwrap(AppUpdateOffer offer) =>
        offer.Handle as UpdateInfo ?? throw new ArgumentException("다른 업데이트 관리자가 만든 업데이트입니다.", nameof(offer));
}
