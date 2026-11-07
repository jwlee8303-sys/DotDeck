using System.Reflection;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using DotDeck.App.Docking;
using DotDeck.App.Infrastructure.Windows;
using DotDeck.App.ViewModels;
using DotDeck.App.Views;
using DotDeck.Core.Explorer;
using DotDeck.Core.Files;
using DotDeck.Core.Logging;
using DotDeck.Core.Paths;
using DotDeck.Core.Platform;
using DotDeck.Core.Projects;

namespace DotDeck.App;

/// <summary>Composition root: resolves paths, creates services and the shell, wires global error handling.</summary>
public partial class App : Application
{
    private FileAppLog? _log;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths paths;
        try
        {
            paths = AppPathsResolver.Resolve(e.Args, SystemProcessEnvironment.Instance);
        }
        catch (AppPathsException ex)
        {
            MessageBox.Show(
                $"시작 옵션이 올바르지 않습니다.\n\n{ex.Message}\n\n사용법: DotDeck.exe [--home <폴더>] [--data <폴더>]",
                "DotDeck",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        _log = new FileAppLog(paths.LogsDir, TimeProvider.System);
        _log.CleanupOldFiles();

        try
        {
            var version = GetVersion();
            _log.Info($"DotDeck {version} 시작 (홈: {paths.Home}, 모드: {paths.HomeSource})");

            var store = PhysicalFileStore.Instance;
            var registry = new ProjectRegistry(store, paths.ProjectsFile, TimeProvider.System, _log);
            var load = registry.Load();
            if (load.Message is { } loadMessage)
            {
                // Corrupt/newer/unreadable project lists are recoverable: tell the user, then continue.
                MessageBox.Show(loadMessage, "DotDeck — 프로젝트 목록", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            var services = new ShellServices(
                paths,
                _log,
                store,
                registry,
                new SessionStateStore(store, paths.SessionFile, _log),
                new ClipboardService(),
                new ShellLauncher(),
                new DialogService());
            var shell = new ShellViewModel(services, version, IsAdministrator());

            var window = new MainWindow(shell, new LayoutPersistence(paths.LayoutDir, _log));
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FailStartup(ex);
            return;
        }

        // Registered only after the main window exists: startup failures must terminate, not be swallowed.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    private void FailStartup(Exception ex)
    {
        _log?.Error("시작 실패", ex);
        MessageBox.Show(
            $"DotDeck을 시작하지 못했습니다.\n\n{ex.Message}",
            "DotDeck",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        Shutdown(1);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.Info("DotDeck 종료");
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (MainWindow is not { IsLoaded: true })
        {
            e.Handled = true;
            FailStartup(e.Exception);
            return;
        }

        // Keep the app alive (SPEC 9: I/O failures are reported, the app continues).
        _log?.Error("처리되지 않은 예외", e.Exception);
        MessageBox.Show(
            $"예기치 않은 오류가 발생했습니다.\n\n{e.Exception.Message}\n\n자세한 내용은 출력 패널과 로그를 확인하세요.",
            "DotDeck",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static string GetVersion() =>
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
