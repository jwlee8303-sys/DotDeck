using System.Reflection;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using DotDeck.App.Docking;
using DotDeck.App.Infrastructure.Windows;
using DotDeck.App.ViewModels;
using DotDeck.App.Views;
using DotDeck.Core.Documents;
using DotDeck.Core.Explorer;
using DotDeck.Core.FileOperations;
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
    private DotDeck.Core.AiCli.Sessions.AiSessionManager? _aiSessions;
    private ShellViewModel? _shell;

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

            foreach (var error in Editor.HighlightingCatalog.Validate())
            {
                _log.Warning($"구문 강조 정의를 불러오지 못했습니다: {error}");
            }

            WebViewEnvironment.Initialize(paths.WebView2DataDir, System.IO.Path.Combine(paths.LocalDataDir, "preview"));

            var store = PhysicalFileStore.Instance;
            var registry = new ProjectRegistry(store, paths.ProjectsFile, TimeProvider.System, _log);
            var load = registry.Load();
            if (load.Message is { } loadMessage)
            {
                // Corrupt/newer/unreadable project lists are recoverable: tell the user, then continue.
                MessageBox.Show(loadMessage, "DotDeck — 프로젝트 목록", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            var settings = new DotDeck.Core.Settings.AppSettingsStore(store, paths.SettingsFile).Load();
            Themes.ThemeManager.ApplyEditorFontSize(settings.EditorFontSize);
            Themes.ThemeManager.Apply(settings.Theme == "Light" ? Themes.ThemeKind.Light : Themes.ThemeKind.Dark);
            Core.Security.SecretPresentation.ScreenShareMode = settings.ScreenShareMode;
            var backups = new BackupStore(store, settings.BackupDirectory ?? paths.BackupsDir, TimeProvider.System, settings.BackupRetention, new DpapiBackupProtector());
            var services = new ShellServices(
                paths,
                _log,
                store,
                registry,
                new SessionStateStore(store, paths.SessionFile, _log),
                new ClipboardService(),
                new ShellLauncher(),
                new DialogService(),
                new FileOperationService(store, new WindowsRecycleBin(_log), new FileOperationHistory(), backups),
                new TextFileReader(store),
                new SafeWriter(store, backups),
                backups,
                CreateEnvironmentStore(paths, store));
            var shell = new ShellViewModel(services, version, IsAdministrator());
            backups.IsAutomaticBackupExcluded = path => shell.Exclusions.BackupPolicy.IsExcluded(path);
            shell.AttachAiSessions(CreateAiSessionManager(paths, store, registry), registry);
            shell.AttachTerminals(new JobProcessLauncher(), () => DotDeck.Core.AiCli.Launch.ShellTerminal.Locate(SystemProcessEnvironment.Instance, store), paths);
            shell.AttachUpdater(new VelopackAppUpdater(Environment.GetEnvironmentVariable(VelopackAppUpdater.FeedVariable)));
            _shell = shell;

            _ = Task.Run(() =>
            {
                try { DotDeck.Core.AiCli.Sessions.SessionRetention.Prune(paths.AiSessionsDir, DateTimeOffset.UtcNow); }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
                { _log.Warning("이전 AI 세션 폴더 정리를 완료하지 못했습니다."); }
            });

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

    private DotDeck.Core.AiCli.Sessions.AiSessionManager CreateAiSessionManager(AppPaths paths, IFileStore store, ProjectRegistry registry)
    {
        var presets = new DotDeck.Core.AiCli.Presets.SessionPresetStore(store, paths.AiSessionsFile);
        if (presets.Load() is DotDeck.Core.AiCli.Presets.PresetStoreLoadStatus.RecoveredFromCorrupt)
        {
            _log?.Warning($"손상된 세션 프리셋 파일을 보존하고 새로 시작합니다: {presets.CorruptCopyPath}");
        }

        var locator = new DotDeck.Core.AiCli.Launch.CliLocator(SystemProcessEnvironment.Instance, store);
        _aiSessions = new DotDeck.Core.AiCli.Sessions.AiSessionManager(new DotDeck.Core.AiCli.Sessions.AiSessionEnvironment
        {
            Paths = paths,
            Store = store,
            Projects = () => registry.Projects,
            Launcher = new JobProcessLauncher(),
            TerminalLauncher = new JobProcessLauncher(),
            Locate = cli => locator.Find(cli),
            GuardExecutable = Environment.ProcessPath ?? throw new InvalidOperationException("DotDeck 실행 파일 경로를 알 수 없습니다."),
            Presets = presets,
            BackupProtector = new DpapiBackupProtector(),
            RecycleBin = _log is null ? null : new WindowsRecycleBin(_log),
            CodexSandboxMode = () => DotDeck.Core.AiCli.Launch.CodexSandboxPolicy.ReadMode(store, paths.CodexHome),
            FinalPathResolver = WindowsFinalPathResolver.Instance,
            StartMcpHost = session =>
            {
                var tools = new DotDeck.Core.AiCli.Mcp.SessionTools(
                    () => session.State == DotDeck.Core.AiCli.Sessions.AiSessionState.Running ? session.Scope : null,
                    () => registry.Projects, store, WindowsFinalPathResolver.Instance,
                    ct => Dispatcher.InvokeAsync(() => _shell?.ReadAiEditorContext() ?? new DotDeck.Core.AiCli.Mcp.EditorContext([], null), DispatcherPriority.Normal, ct).Task,
                    (path, ct) => Dispatcher.InvokeAsync(() => _shell?.OpenAiFileAsync(session, path) ?? Task.CompletedTask, DispatcherPriority.Normal, ct).Task.Unwrap(),
                    session.Request.GlobalExtensions, session.Request.IncludeOpenDocuments);
                var host = new NamedPipeMcpHost(session.Directory, session.Id, new LiveSessionHost(session, tools));
                try
                {
                    System.IO.File.WriteAllText(System.IO.Path.Combine(session.Directory, NamedPipeMcpHost.ConfigFile),
                        NamedPipeMcpHost.BuildConfig(Environment.ProcessPath!, session.Directory, session.Id), new System.Text.UTF8Encoding(false));
                    return host;
                }
                catch { host.Dispose(); throw; }
            },
            BackupExcluded = path => _shell?.Exclusions.BackupPolicy.IsExcluded(path) ?? false,
            ReviewExcluded = path => _shell?.Exclusions.ReviewPolicy.IsExcluded(path) ?? false,
            ReviewDirectoryExcluded = path => _shell?.Exclusions.ReviewPolicy.IsDirectoryExcluded(path) ?? false,
        });
        return _aiSessions;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Running CLI trees end with their jobs (also if this never runs: the OS closes the job handles).
        _shell?.CloseAllTerminals();
        _aiSessions?.Dispose();
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

    // Sandbox mode (--home / DOTDECK_HOME) never touches the real registry: variables live in a JSON file there.
    private static DotDeck.Core.SystemConfig.IEnvironmentStore CreateEnvironmentStore(AppPaths paths, PhysicalFileStore store) =>
        paths.IsHomeOverridden
            ? new DotDeck.Core.SystemConfig.FileEnvironmentStore(store, System.IO.Path.Combine(paths.Home, ".dotdeck-environment.json"), IsAdministrator())
            : new Infrastructure.Windows.RegistryEnvironmentStore(IsAdministrator());

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
