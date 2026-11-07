using System.ComponentModel;
using System.Windows;
using DotDeck.App.Docking;
using DotDeck.App.Infrastructure.Windows;
using DotDeck.App.Themes;
using DotDeck.App.ViewModels;
using DotDeck.Core.Layout;
using System.Windows.Controls;

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
        _shell.RestartElevatedRequested += (_, _) => RestartElevated();

        // Coming back to DotDeck is when external edits (another editor, git) are most likely: re-check open files.
        Activated += (_, _) => _ = _shell.CheckExternalChangesAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _shell.PrepareLayoutContent();
        _restoring = true;
        try
        {
            // Tabs first, so the saved layout finds their ContentIds.
            await _shell.RestoreDocumentsAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _shell.ReportError("지난 세션의 탭을 복원하는 중 오류가 발생했습니다.", ex);
        }
        finally
        {
            _restoring = false;
        }

        if (_isClosed)
        {
            return;
        }

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

    private bool _closeConfirmed;

    // Saves tabs and layout first so the elevated instance restores them, then starts it (UAC) and closes.
    private void RestartElevated()
    {
        if (_shell.HasUnsavedDocuments)
        {
            MessageBox.Show(this, "저장하지 않은 문서가 있습니다. 저장하거나 닫은 뒤 다시 시도하세요.", "관리자 권한으로 다시 시작",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _layout.Save(DockManager);
        _shell.SaveSession();
        var args = string.Join(' ', Environment.GetCommandLineArgs().Skip(1).Select(a => $"\"{a.TrimEnd('\\')}\""));
        try
        {
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, args)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return; // UAC prompt declined
        }

        _closeConfirmed = true;
        Close();
    }

    // Closing before the session was restored must not overwrite it with the partial state.
    private bool _restoring;
    private bool _isClosed;

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel && !_closeConfirmed && _shell.HasUnsavedDocuments)
        {
            // Saving is asynchronous: cancel this close, ask, then close again.
            e.Cancel = true;
            _ = ConfirmAndCloseAsync();
            return;
        }

        if (!e.Cancel)
        {
            _isClosed = true;
            if (!_restoring)
            {
                _layout.Save(DockManager);
                _shell.SaveSession();
            }
        }
    }

    private async Task ConfirmAndCloseAsync()
    {
        try
        {
            if (await _shell.ConfirmCloseAllAsync())
            {
                _closeConfirmed = true;
                Close();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _shell.ReportError("종료 중 오류가 발생했습니다.", ex);
        }
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    // Layout commands wait for the startup restore, which applies last.xml when it finishes.
    private bool LayoutBusy()
    {
        if (_restoring)
        {
            _shell.StatusMessage = "지난 세션을 복원하는 중입니다. 잠시 후 다시 시도하세요.";
        }

        return _restoring;
    }

    private void OnTemplateSubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, TemplateMenu))
        {
            return;
        }

        TemplateMenu.Items.Clear();
        var builtInDone = false;
        foreach (var template in _shell.LoadTemplates())
        {
            if (!template.IsBuiltIn && !builtInDone)
            {
                TemplateMenu.Items.Add(new Separator());
                builtInDone = true;
            }

            var item = new MenuItem { Header = new TextBlock { Text = template.Name } };
            item.Click += (_, _) => _shell.NewFromTemplate(template);
            TemplateMenu.Items.Add(item);
        }

        TemplateMenu.Items.Add(new Separator());
        var open = new MenuItem { Header = "템플릿 폴더 열기..." };
        open.Click += (_, _) =>
        {
            System.IO.Directory.CreateDirectory(_shell.Paths.TemplatesDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_shell.Paths.TemplatesDir) { UseShellExecute = true });
        };
        TemplateMenu.Items.Add(open);
    }

    private void OnSaveNamedLayoutClick(object sender, RoutedEventArgs e)
    {
        if (LayoutBusy())
        {
            return;
        }

        var name = _shell.Dialogs.AskText("레이아웃 저장", "레이아웃 이름 (같은 이름이 있으면 덮어씁니다)", string.Empty, LayoutNames.Validate);
        if (name is null)
        {
            return;
        }

        if (_layout.ListNamed().Contains(name, StringComparer.OrdinalIgnoreCase)
            && !_shell.Dialogs.Confirm("레이아웃 저장", $"'{name}' 레이아웃이 이미 있습니다. 덮어쓸까요?"))
        {
            return;
        }

        _shell.StatusMessage = _layout.SaveNamed(DockManager, name)
            ? $"레이아웃을 저장했습니다: {name}"
            : "레이아웃을 저장하지 못했습니다. 자세한 내용은 출력 패널을 확인하세요.";
    }

    private void OnLayoutSubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menu || !ReferenceEquals(e.OriginalSource, menu))
        {
            return;
        }

        var delete = ReferenceEquals(menu, DeleteLayoutMenu);
        menu.Items.Clear();
        var names = _layout.ListNamed();
        if (names.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "(없음)", IsEnabled = false });
            return;
        }

        foreach (var name in names)
        {
            // Header as plain text: an underscore in a name must not become an access key.
            var item = new MenuItem { Header = new TextBlock { Text = name } };
            item.Click += (_, _) =>
            {
                if (delete)
                {
                    DeleteNamedLayout(name);
                }
                else
                {
                    LoadNamedLayout(name);
                }
            };
            menu.Items.Add(item);
        }
    }

    private void LoadNamedLayout(string name)
    {
        if (LayoutBusy())
        {
            return;
        }

        _shell.StatusMessage = _layout.TryLoadNamed(DockManager, _shell, name) == LayoutRestoreResult.Restored
            ? $"레이아웃을 불러왔습니다: {name}"
            : "레이아웃을 불러오지 못해 기본 레이아웃을 사용합니다. 자세한 내용은 출력 패널을 확인하세요.";
    }

    private void DeleteNamedLayout(string name)
    {
        if (_shell.Dialogs.Confirm("레이아웃 삭제", $"'{name}' 레이아웃을 삭제할까요?"))
        {
            _shell.StatusMessage = _layout.DeleteNamed(name)
                ? $"레이아웃을 삭제했습니다: {name}"
                : "레이아웃을 삭제하지 못했습니다.";
        }
    }

    private void OnResetLayoutClick(object sender, RoutedEventArgs e)
    {
        if (LayoutBusy())
        {
            return;
        }

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
