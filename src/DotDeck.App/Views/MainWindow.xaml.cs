using System.ComponentModel;
using System.Windows;
using DotDeck.App.Docking;
using DotDeck.App.Infrastructure.Windows;
using DotDeck.App.Themes;
using DotDeck.App.ViewModels;

namespace DotDeck.App.Views;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;
    private readonly LayoutPersistence _layout;

    public MainWindow(ShellViewModel shell, LayoutPersistence layout)
    {
        _shell = shell;
        _layout = layout;
        DataContext = shell;
        InitializeComponent();

        DarkTitleBar.Attach(this);
        ThemeManager.ApplyDocking(DockManager);
        DockManager.LayoutFloatingWindowControlCreated += (_, e) =>
        {
            ThemeManager.ApplyFloating(e.LayoutFloatingWindowControl);
            DarkTitleBar.Attach(e.LayoutFloatingWindowControl);
        };

        // Start from the code-built default so "reset layout" and first run share one definition.
        DockManager.Layout = DefaultLayout.Create();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (_layout.TryRestore(DockManager, _shell) == LayoutRestoreResult.Failed)
        {
            _shell.StatusMessage = "레이아웃 복원 실패 — 기본 레이아웃을 사용합니다. 자세한 내용은 출력 패널을 확인하세요.";
        }

        _ = InitializeShellAsync();
    }

    private async Task InitializeShellAsync()
    {
        try
        {
            await _shell.InitializeAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _shell.ReportError("프로젝트를 여는 중 오류가 발생했습니다.", ex);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel)
        {
            _layout.Save(DockManager);
            _shell.SaveSession();
        }
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    private void OnResetLayoutClick(object sender, RoutedEventArgs e)
    {
        LayoutPersistence.Reset(DockManager, _shell);
        _shell.StatusMessage = "레이아웃을 초기화했습니다.";
    }

    private void OnAboutClick(object sender, RoutedEventArgs e) =>
        MessageBox.Show(
            this,
            $"DotDeck {_shell.Version}\n개발 환경 설정 파일·문서 에디터\n\n홈: {_shell.Paths.Home}\n데이터: {_shell.Paths.LocalDataDir}",
            "DotDeck 정보",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
}
