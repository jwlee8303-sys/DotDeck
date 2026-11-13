using DotDeck.App.Services;
using DotDeck.Core.Logging;

namespace DotDeck.App.ViewModels;

public enum UpdateCheckTrigger
{
    /// <summary>Background check after startup: stays silent unless a newer release exists.</summary>
    Startup,

    /// <summary>Help menu: reports every outcome.</summary>
    Manual,
}

/// <summary>The UI the update flow needs (implemented by the shell; faked in tests).</summary>
public interface IUpdateUi
{
    /// <summary>Shows the offer; true for "지금 업데이트", false for "나중에".</summary>
    bool AskInstall(AppUpdateOffer offer);

    void ShowInfo(string message);

    void ShowError(string message);

    void SetStatus(string message);

    /// <summary>Saves or discards open work and stops sessions (the usual exit questions). False cancels.</summary>
    Task<bool> ConfirmExitAsync();

    /// <summary>Closes the app without asking again.</summary>
    void RequestExit();
}

/// <summary>
/// Check → ask → download → confirm exit → apply on exit. Failures never block the app; "나중에" and a cancelled
/// exit keep it running (a downloaded release is applied at the next start), and the menu can retry at any time.
/// </summary>
public sealed class AppUpdateCoordinator(IAppUpdater updater, IUpdateUi ui, IAppLog log, TimeSpan? checkTimeout = null)
{
    private readonly TimeSpan _checkTimeout = checkTimeout ?? TimeSpan.FromSeconds(30);

    public bool IsBusy { get; private set; }

    public async Task CheckAsync(UpdateCheckTrigger trigger)
    {
        var manual = trigger == UpdateCheckTrigger.Manual;
        if (IsBusy)
        {
            if (manual) ui.SetStatus("업데이트를 이미 처리하고 있습니다.");
            return;
        }

        if (!updater.IsInstalled)
        {
            if (manual) ui.ShowInfo("설치 파일(또는 Portable ZIP)로 받은 DotDeck에서만 업데이트를 확인할 수 있습니다.\n(개발 실행과 v1.4.1 이하의 ZIP 실행 파일은 제외)");
            return;
        }

        IsBusy = true;
        try
        {
            await RunAsync(manual).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunAsync(bool manual)
    {
        if (manual) ui.SetStatus("업데이트를 확인하는 중...");

        AppUpdateOffer? offer;
        try
        {
            using var timeout = new CancellationTokenSource(_checkTimeout);
            offer = await updater.CheckAsync(timeout.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.Warning("업데이트를 확인하지 못했습니다.", ex);
            if (manual)
            {
                ui.SetStatus("");
                ui.ShowError("업데이트를 확인하지 못했습니다. 네트워크 연결을 확인한 뒤 다시 시도하세요.");
            }

            return;
        }

        if (offer is null)
        {
            if (manual)
            {
                ui.SetStatus("");
                ui.ShowInfo("최신 버전을 사용하고 있습니다.");
            }

            return;
        }

        log.Info($"새 버전 {offer.NewVersion} 확인 (현재 {offer.CurrentVersion})");
        if (!ui.AskInstall(offer))
        {
            ui.SetStatus($"새 버전 {offer.NewVersion}은 나중에 설치합니다. 도움말 > 업데이트 확인에서 다시 설치할 수 있습니다.");
            return;
        }

        try
        {
            ui.SetStatus($"업데이트 {offer.NewVersion} 내려받는 중... 0%");
            await updater.DownloadAsync(offer, percent => ui.SetStatus($"업데이트 {offer.NewVersion} 내려받는 중... {percent}%"), CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.Warning("업데이트를 내려받지 못했습니다.", ex);
            ui.SetStatus("");
            ui.ShowError("업데이트를 내려받지 못했습니다. 도움말 > 업데이트 확인에서 다시 시도하세요.");
            return;
        }

        if (!await ui.ConfirmExitAsync().ConfigureAwait(true))
        {
            ui.SetStatus($"업데이트 {offer.NewVersion}을 내려받았습니다. 다음에 DotDeck을 시작할 때 적용됩니다.");
            return;
        }

        try
        {
            updater.ApplyOnExit(offer);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.Warning("업데이트 적용을 시작하지 못했습니다.", ex);
            ui.ShowError("업데이트 적용을 시작하지 못했습니다. 다음에 DotDeck을 시작할 때 다시 적용합니다.");
            return;
        }

        log.Info($"업데이트 {offer.NewVersion} 적용을 위해 종료합니다.");
        ui.RequestExit();
    }
}
