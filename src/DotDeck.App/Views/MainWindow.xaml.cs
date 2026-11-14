using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using DotDeck.App.Docking;
using DotDeck.App.Infrastructure.Windows;
using DotDeck.App.Themes;
using DotDeck.App.ViewModels;
using DotDeck.Core.Layout;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AvalonDock.Controls;
using AvalonDock.Layout;
using DotDeck.App.ViewModels.Tools;

namespace DotDeck.App.Views;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;
    private readonly LayoutPersistence _layout;
    private readonly LayoutInitializer _placement;
    private readonly ExtensionWindows _extensions;
    private TrayIcon? _tray;

    /// <summary>X hides the window to the tray instead of exiting (settings; File > 끝내기 still exits).</summary>
    public bool CloseToTray { get; set; }

    static MainWindow()
    {
        // Every floating window, however it was made (drag, Float(), restored layout — only drags raise
        // LayoutFloatingWindowControlCreated). Class handlers run before AvalonDock's own Loaded handler.
        EventManager.RegisterClassHandler(typeof(LayoutFloatingWindowControl), LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                var window = (LayoutFloatingWindowControl)sender;
                DetachFromMainWindow(window);
                if (window is LayoutDocumentFloatingWindowControl extension)
                {
                    MarkCaption(extension);
                    (Application.Current?.MainWindow as MainWindow)?.ScheduleExtensionIndicator();
                }
            }));
    }

    // Height of the theme's header row (VS2022 theme: header MinHeight 32) and the resize strip kept above it.
    private const double ExtensionHeaderHeight = 32;
    private const double ExtensionResizeBorder = 6;

    // The VS2022 theme gives the window a 20px resize border and puts the 32px drag area below it, so the drag area
    // ran 20px past the header into the tabs. Shrink the border so the drag area is the header, and paint the header
    // in the status bar color so the part that moves the window is visible.
    private static void MarkCaption(LayoutDocumentFloatingWindowControl window)
    {
        if (Microsoft.Windows.Shell.WindowChrome.GetWindowChrome(window) is { } chrome)
        {
            var fitted = (Microsoft.Windows.Shell.WindowChrome)chrome.Clone();
            fitted.ResizeBorderThickness = new Thickness(ExtensionResizeBorder);
            fitted.CaptionHeight = ExtensionHeaderHeight - ExtensionResizeBorder;
            Microsoft.Windows.Shell.WindowChrome.SetWindowChrome(window, fitted);
        }

        if (window.Template?.FindName("Header", window) is Border header)
        {
            header.SetResourceReference(Border.BackgroundProperty, "DD.Brush.StatusBar.Background");
            header.SetResourceReference(TextElement.ForegroundProperty, "DD.Brush.Text.OnAccent");
            if (header.Child is Grid buttons)
            {
                AddHideButton(window, buttons);
            }
        }
    }

    private const string HideButtonTag = "DotDeck.HideExtensionWindow";

    // "숨기기" left of the theme's maximize/close buttons: a new column 1 shifts them right.
    private static void AddHideButton(LayoutDocumentFloatingWindowControl window, Grid header)
    {
        if (header.Children.OfType<Button>().Any(b => Equals(b.Tag, HideButtonTag)))
        {
            return;
        }

        foreach (UIElement child in header.Children)
        {
            if (Grid.GetColumn(child) >= 1)
            {
                Grid.SetColumn(child, Grid.GetColumn(child) + 1);
            }
        }

        header.ColumnDefinitions.Insert(1, new ColumnDefinition { Width = GridLength.Auto });
        var glyph = new System.Windows.Shapes.Rectangle { Width = 10, Height = 1, SnapsToDevicePixels = true };
        glyph.SetBinding(System.Windows.Shapes.Shape.FillProperty,
            new Binding(nameof(Button.Foreground)) { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1) });
        var hide = new Button
        {
            Tag = HideButtonTag,
            Content = glyph,
            ToolTip = "확장 창 숨기기 (창 → 확장 창 숨기기/표시에서 다시 표시, 실행 중인 터미널·CLI는 계속 동작)",
        };
        hide.SetResourceReference(StyleProperty, "DD.CaptionButton");
        Microsoft.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(hide, true);
        Grid.SetColumn(hide, 1);
        hide.Click += (_, _) =>
        {
            if (window.Model is LayoutDocumentFloatingWindow model && Application.Current?.MainWindow is MainWindow main)
            {
                main.HideExtension(model);
            }
        };
        header.Children.Add(hide);
    }

    // Not owned by the main window: an owned window always stays above its owner, so clicking the main window
    // could never bring it in front of an overlapping extension window. Whichever window is clicked comes to the front.
    private static void DetachFromMainWindow(LayoutFloatingWindowControl window)
    {
        window.OwnedByDockingManagerWindow = false;
        if (window is LayoutDocumentFloatingWindowControl)
        {
            // An extension window can now end up behind the main window; the taskbar keeps it reachable.
            window.ShowInTaskbar = true;
        }
    }

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
        // Tools go to their preferred side panes until the startup layout has been restored.
        _placement = (LayoutInitializer)DockManager.LayoutUpdateStrategy;
        _placement.ExplicitAnchor = () => _shell.PlacementAnchor;
        _extensions = new ExtensionWindows(DockManager, _shell);
        DockManager.LayoutChanged += (_, _) =>
        {
            DockManager.Layout.Updated += (_, _) => ScheduleExtensionIndicator();
            ScheduleExtensionIndicator();
        };
        DockManager.Layout = DefaultLayout.Create();
        DockManager.ActiveContentChanged += OnDockActiveContentChanged;
        Loaded += OnLoaded;
        _shell.RestartElevatedRequested += (_, _) => RestartElevated();
        // The update flow already asked the exit questions; the updater waits for this process to exit.
        _shell.ExitRequested += (_, _) =>
        {
            _closeConfirmed = true;
            Close();
        };
        _shell.SettingsChanged += (_, settings) =>
        {
            CloseToTray = settings.CloseToTray;
            ThemeManager.ApplyEditorFontSize(settings.EditorFontSize);
            ThemeManager.Apply(settings.Theme == "Light" ? ThemeKind.Light : ThemeKind.Dark);
        };
        ThemeManager.Changed += (_, _) =>
        {
            ThemeManager.ApplyDocking(DockManager);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, EnsureTabMenus);

            // A new theme can re-apply the floating window template: header color and hide button again.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                foreach (var extension in DockManager.FloatingWindows.OfType<LayoutDocumentFloatingWindowControl>())
                {
                    MarkCaption(extension);
                }
            });
        };

        // Windows sign-out/shutdown must really exit, not hide to the tray.
        Application.Current.SessionEnding += (_, _) => _exitRequested = true;

        // Coming back to DotDeck is when external edits (another editor, git) are most likely: re-check open files.
        Activated += (_, _) => _ = _shell.CheckExternalChangesAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        // The tray icon is there from startup (not only after the window was hidden to it) until the app exits.
        _tray ??= CreateTray();
        _tray.Show();

        _shell.PrepareLayoutContent();
        _restoring = true;
        try
        {
            // Tabs first, so the saved layout finds their ContentIds.
            await _shell.RestoreDocumentsAsync();
            await _shell.RestoreTerminalsAsync();
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

        if (WithPreferredLocations(() => _layout.TryRestore(DockManager, _shell)) == LayoutRestoreResult.Failed)
        {
            _shell.StatusMessage = "레이아웃 복원 실패 — 기본 레이아웃을 사용합니다. 자세한 내용은 출력 패널을 확인하세요.";
        }

        // From now on new panes open next to the same kind (or beside the project area for the first one).
        _placement.UsePreferredLocations = false;
        EnsureTabMenus();

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

        if (!_isClosed)
        {
            // The coordinator handles its own failures; this only guards against a bug escaping it.
            try { await _shell.CheckForUpdatesOnStartupAsync(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _shell.ReportError("업데이트 확인 중 오류가 발생했습니다.", ex); }
        }
    }

    private bool _closeConfirmed;

    // Set by File > 끝내기, the tray menu and sign-out: these close for real even with CloseToTray on.
    private bool _exitRequested;

    // Extension windows and floating tool windows hidden along with the main window, shown again on restore.
    private readonly List<Window> _hiddenFloatingWindows = [];

    private void HideToTray()
    {
        _tray ??= CreateTray();
        _tray.Show();

        // Floating windows are separate windows: hiding the main window leaves them on screen. SetCurrentValue keeps
        // AvalonDock's own visibility binding of floating tool windows.
        foreach (var window in DockManager.FloatingWindows.Where(w => w.IsVisible))
        {
            _hiddenFloatingWindows.Add(window);
            window.SetCurrentValue(VisibilityProperty, Visibility.Hidden);
        }

        Hide();
        _shell.StatusMessage = "DotDeck이 트레이에서 실행 중입니다.";
    }

    private TrayIcon CreateTray()
    {
        var tray = new TrayIcon("DotDeck");
        tray.OpenRequested += (_, _) => RestoreFromTray();
        tray.ExitRequested += (_, _) =>
        {
            RestoreFromTray(); // unsaved-changes questions need a visible window
            ExitApplication();
        };
        return tray;
    }

    internal void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        // After the main window, so they come back above it. Closed meanwhile (exit, docking) → skipped.
        foreach (var window in _hiddenFloatingWindows.Where(w => DockManager.FloatingWindows.Contains(w)))
        {
            window.SetCurrentValue(VisibilityProperty, Visibility.Visible);
        }

        _hiddenFloatingWindows.Clear();

        Activate();
        _ = Focus();
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        Close();
    }

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
        var launchArguments = ((App)Application.Current).LaunchArguments ?? Environment.GetCommandLineArgs().Skip(1).ToArray();
        var args = string.Join(' ', launchArguments.Select(a => $"\"{a.TrimEnd('\\')}\""));
        args = $"{SingleInstance.RestartSwitch} {Environment.ProcessId} {args}";
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
        if (!e.Cancel && CloseToTray && !_exitRequested && !_closeConfirmed)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        if (!e.Cancel && _restoring)
        {
            e.Cancel = true;
            _exitRequested = false;
            _shell.StatusMessage = "지난 세션을 복원하는 중입니다. 완료 후 종료하세요.";
            return;
        }

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
            _tray?.Dispose();
            _tray = null;
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
            else
            {
                _exitRequested = false;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _shell.ReportError("종료 중 오류가 발생했습니다.", ex);
        }
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => ExitApplication();

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

        _shell.StatusMessage = WithPreferredLocations(() => _layout.TryLoadNamed(DockManager, _shell, name)) == LayoutRestoreResult.Restored
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

        WithPreferredLocations(() =>
        {
            LayoutPersistence.Reset(DockManager, _shell);
            return true;
        });
        _shell.StatusMessage = "레이아웃을 초기화했습니다.";
    }

    // ===== Placement and extension windows =====

    // Building a whole layout (restore, named layout, reset) places tools by their preferred side, not by kind.
    private T WithPreferredLocations<T>(Func<T> action)
    {
        var previous = _placement.UsePreferredLocations;
        _placement.UsePreferredLocations = true;
        try
        {
            return action();
        }
        finally
        {
            _placement.UsePreferredLocations = previous;
        }
    }

    private void OnDockActiveContentChanged(object? sender, EventArgs e)
    {
        if (DockManager.ActiveContent is { } active)
        {
            var content = _extensions.Find(active);
            _placement.NoteActive(content);

            // A tab of a hidden extension window was activated (AI monitor, "확장 창으로 이동", open file…): show it.
            if (IsVisible && content?.FindParent<LayoutDocumentFloatingWindow>() is { } window && _extensions.IsHidden(window))
            {
                _extensions.Show(window);
            }
        }

        EnsureTabMenus();
    }

    // ===== Extension window indicator (right end of the menu bar) =====

    private bool _indicatorScheduled;

    // Extension window controls whose show/hide already refreshes the indicator.
    private readonly HashSet<Window> _indicatorWatched = [];

    // Layout updates come in bursts: rebuild once after they settle.
    internal void ScheduleExtensionIndicator()
    {
        if (_indicatorScheduled)
        {
            return;
        }

        _indicatorScheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _indicatorScheduled = false;
            RefreshExtensionIndicator();
        });
    }

    /// <summary>One toggle per extension window: highlighted while hidden, dimmed while shown; a click switches.</summary>
    private void RefreshExtensionIndicator()
    {
        ExtensionIndicator.Children.Clear();
        _indicatorWatched.RemoveWhere(w => !DockManager.FloatingWindows.Contains(w));

        var windows = _extensions.List();
        ExtensionIndicator.Visibility = windows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < windows.Count; i++)
        {
            var window = windows[i];
            var index = i;
            if (_extensions.ControlOf(window) is { } control && _indicatorWatched.Add(control))
            {
                control.IsVisibleChanged += (_, _) => ScheduleExtensionIndicator();
            }

            var hidden = _extensions.IsHidden(window);
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(new TextBlock { Text = "", Margin = new Thickness(0, 0, 4, 0), Style = (Style)FindResource("DD.Glyph") });
            label.Children.Add(new TextBlock { Text = string.Create(CultureInfo.InvariantCulture, $"확장 {i + 1}"), VerticalAlignment = VerticalAlignment.Center });

            // Computed when shown: the window's title follows its selected tab.
            string Tip() =>
                $"{ExtensionWindows.NameOf(window, index)}\n{(_extensions.IsHidden(window) ? "숨겨져 있음 — 클릭하면 표시" : "표시 중 — 클릭하면 숨기기")} (터미널·CLI는 계속 실행)";
            // Hidden windows stand out (they are out of sight); shown ones are dimmed.
            var toggle = new ToggleButton { Content = label, IsChecked = hidden, Padding = new Thickness(4, 1, 4, 1), ToolTip = Tip() };
            toggle.SetResourceReference(StyleProperty, ToolBar.ToggleButtonStyleKey);
            toggle.SetResourceReference(ForegroundProperty, hidden ? "DD.Brush.Text" : "DD.Brush.Text.Secondary");
            toggle.ToolTipOpening += (_, _) => toggle.ToolTip = Tip();
            toggle.Click += (_, _) =>
            {
                if (_extensions.IsHidden(window))
                {
                    _extensions.Show(window);
                }
                else
                {
                    HideExtension(window);
                }

                ScheduleExtensionIndicator();
            };
            ExtensionIndicator.Children.Add(toggle);
        }
    }

    /// <summary>Hides an extension window; its terminals and CLIs keep running.</summary>
    internal void HideExtension(LayoutDocumentFloatingWindow window)
    {
        var holdsActive = DockManager.ActiveContent is { } active
            && _extensions.Find(active)?.FindParent<LayoutDocumentFloatingWindow>() is { } owner
            && ReferenceEquals(owner, window);

        _extensions.Hide(window);
        if (holdsActive)
        {
            // The active tab went out of sight: continue in the main window's selected tab.
            var next = DockManager.Layout.Descendents().OfType<LayoutContent>()
                .FirstOrDefault(c => c.IsSelected && c.FindParent<LayoutFloatingWindow>() is null);
            DockManager.ActiveContent = next?.Content;
            Activate();
        }

        var index = _extensions.List().ToList().IndexOf(window);
        _shell.StatusMessage = $"{ExtensionWindows.NameOf(window, index)}을(를) 숨겼습니다. 창 → 확장 창 숨기기/표시에서 다시 표시합니다.";
    }

    private void OnExtensionVisibilitySubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, ExtensionVisibilityMenu))
        {
            return;
        }

        var menu = ExtensionVisibilityMenu;
        menu.Items.Clear();
        var windows = _extensions.List();
        if (windows.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "(확장 창 없음)", IsEnabled = false });
            return;
        }

        for (var i = 0; i < windows.Count; i++)
        {
            var window = windows[i];

            // Checked = shown. Header as plain text: an underscore in a title must not become an access key.
            var item = new MenuItem { Header = new TextBlock { Text = ExtensionWindows.NameOf(window, i) }, IsChecked = !_extensions.IsHidden(window) };
            item.Click += (_, _) =>
            {
                if (_extensions.IsHidden(window))
                {
                    _extensions.Show(window);
                }
                else
                {
                    HideExtension(window);
                }
            };
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var hideAll = new MenuItem { Header = "모두 숨기기(_H)", IsEnabled = windows.Any(w => !_extensions.IsHidden(w)) };
        hideAll.Click += (_, _) =>
        {
            foreach (var window in windows.Where(w => !_extensions.IsHidden(w)))
            {
                HideExtension(window);
            }

            _shell.StatusMessage = "확장 창을 모두 숨겼습니다. 창 → 확장 창 숨기기/표시에서 다시 표시합니다.";
        };
        var showAll = new MenuItem { Header = "모두 표시(_S)", IsEnabled = windows.Any(_extensions.IsHidden) };
        showAll.Click += (_, _) =>
        {
            foreach (var window in windows.Where(_extensions.IsHidden))
            {
                _extensions.Show(window);
            }
        };
        menu.Items.Add(hideAll);
        menu.Items.Add(showAll);
    }

    private void OnNewExtensionWindowClick(object sender, RoutedEventArgs e)
    {
        if (!LayoutBusy() && _extensions.Create() is null)
        {
            _shell.StatusMessage = "확장 창을 열지 못했습니다.";
        }
    }

    private void OnMoveToExtensionSubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, MoveToExtensionMenu))
        {
            FillMoveMenu(MoveToExtensionMenu, DockManager.ActiveContent is { } active ? _extensions.Find(active) : null);
        }
    }

    private const string MoveMenuTag = "DotDeck.MoveToExtension";

    // AvalonDock's own "close all" items act on every document of the whole layout and skip tool panes (terminals).
    private static readonly string[] BuiltInGroupCloseCommands = ["CloseAllButThisCommand", "CloseAllCommand"];

    /// <summary>The tab a tab menu was opened for.</summary>
    private static LayoutContent? ContentOf(ContextMenu menu) => menu.DataContext switch
    {
        LayoutItem layoutItem => layoutItem.LayoutElement as LayoutContent,
        LayoutContent content => content,
        _ => null,
    };

    /// <summary>
    /// Completes AvalonDock's tab menus (a theme change brings new menu instances): "이름 바꾸기" for terminals,
    /// "이 창을 제외하고 모두 닫기"/"모두 닫기" for the clicked tab's group, and "확장 창으로 이동".
    /// </summary>
    private void EnsureTabMenus()
    {
        foreach (var menu in new[] { DockManager.DocumentContextMenu, DockManager.AnchorableContextMenu })
        {
            if (menu is null || menu.ItemsSource is not null
                || menu.Items.OfType<MenuItem>().Any(i => Equals(i.Tag, MoveMenuTag)))
            {
                continue;
            }

            AddRenameItem(menu);
            AddGroupCloseItems(menu);

            var item = new MenuItem { Header = "확장 창으로 이동", Tag = MoveMenuTag };
            item.Items.Add(new MenuItem { Header = "(없음)", IsEnabled = false });
            item.SubmenuOpened += (_, args) =>
            {
                if (ReferenceEquals(args.OriginalSource, item))
                {
                    FillMoveMenu(item, ContentOf(menu));
                }
            };
            menu.Items.Add(new Separator());
            menu.Items.Add(item);
        }
    }

    // "이름 바꾸기" first in the menu, shown for terminal tabs only.
    private void AddRenameItem(ContextMenu menu)
    {
        var rename = new MenuItem { Header = "이름 바꾸기" };
        var separator = new Separator();
        rename.Click += (_, _) =>
        {
            if (ContentOf(menu) is { Content: TerminalPaneViewModel terminal } content)
            {
                BeginTerminalRename(content, terminal);
            }
        };
        menu.Opened += (_, _) =>
        {
            var visibility = ContentOf(menu)?.Content is TerminalPaneViewModel ? Visibility.Visible : Visibility.Collapsed;
            rename.Visibility = visibility;
            separator.Visibility = visibility;
        };
        menu.Items.Insert(0, rename);
        menu.Items.Insert(1, separator);
    }

    // Replaces AvalonDock's "close all" items (document menu) or adds them (tool menu): both close the clicked tab's
    // group only, documents, terminals and tool panes alike.
    private void AddGroupCloseItems(ContextMenu menu)
    {
        var builtIn = menu.Items.OfType<MenuItem>()
            .Where(i => BindingOperations.GetBinding(i, MenuItem.CommandProperty)?.Path?.Path is { } path
                && BuiltInGroupCloseCommands.Contains(path))
            .ToList();
        foreach (var item in builtIn)
        {
            item.Visibility = Visibility.Collapsed;
        }

        var closeOthers = new MenuItem { Header = "이 창을 제외하고 모두 닫기" };
        closeOthers.Click += (_, _) => CloseGroupTabs(ContentOf(menu), keepClicked: true);
        var closeAll = new MenuItem { Header = "모두 닫기" };
        closeAll.Click += (_, _) => CloseGroupTabs(ContentOf(menu), keepClicked: false);
        menu.Opened += (_, _) =>
        {
            var content = ContentOf(menu);
            var group = GroupOf(content);
            closeOthers.IsEnabled = group.Any(c => c != content && c.Content is PaneViewModel pane && ShellViewModel.CanCloseTab(pane));
            closeAll.IsEnabled = group.Any(c => c.Content is PaneViewModel pane && ShellViewModel.CanCloseTab(pane));
        };

        if (builtIn.Count > 0)
        {
            var index = menu.Items.IndexOf(builtIn[^1]) + 1;
            menu.Items.Insert(index, closeOthers);
            menu.Items.Insert(index + 1, closeAll);
        }
        else
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(closeOthers);
            menu.Items.Add(closeAll);
        }
    }

    /// <summary>The tabs of the group (document or tool pane) that holds <paramref name="content"/>.</summary>
    private static List<LayoutContent> GroupOf(LayoutContent? content) => content switch
    {
        null => [],
        { Parent: ILayoutContainer group } => [.. group.Children.OfType<LayoutContent>()],
        _ => [content],
    };

    private void CloseGroupTabs(LayoutContent? clicked, bool keepClicked)
    {
        if (clicked is null || LayoutBusy())
        {
            return;
        }

        var panes = GroupOf(clicked)
            .Where(c => !keepClicked || c != clicked)
            .Select(c => c.Content)
            .OfType<PaneViewModel>()
            .ToList();
        _shell.CloseTabs(panes);
    }

    private void FillMoveMenu(MenuItem menu, LayoutContent? content)
    {
        menu.Items.Clear();
        if (content is null)
        {
            menu.Items.Add(new MenuItem { Header = "(이동할 탭 없음)", IsEnabled = false });
            return;
        }

        var current = content.FindParent<LayoutDocumentFloatingWindow>();
        var windows = _extensions.List();
        for (var i = 0; i < windows.Count; i++)
        {
            var window = windows[i];

            // Header as plain text: an underscore in a title must not become an access key.
            var item = new MenuItem
            {
                Header = new TextBlock { Text = ExtensionWindows.NameOf(window, i) },
                IsEnabled = !ReferenceEquals(window, current),
            };
            item.Click += (_, _) => _extensions.MoveTo(content, window);
            menu.Items.Add(item);
        }

        if (windows.Count > 0)
        {
            menu.Items.Add(new Separator());
        }

        var create = new MenuItem { Header = "새 확장 창(_N)" };
        create.Click += (_, _) =>
        {
            if (!LayoutBusy() && _extensions.Create(content) is null)
            {
                _shell.StatusMessage = "확장 창을 열지 못했습니다.";
            }
        };
        menu.Items.Add(create);
    }

    // ===== Terminal title: rename in place (tab menu "이름 바꾸기") =====

    // The title the tab menu was last opened on: the same title (tab or tool title bar) gets the edit box.
    private TextBox? _lastTitleRenameBox;

    private static TerminalPaneViewModel? TerminalOf(object? sender) =>
        sender is FrameworkElement { DataContext: LayoutContent { Content: TerminalPaneViewModel terminal } } ? terminal : null;

    // Not handled: the tab menu opens as usual.
    private void OnPaneTitleRightClick(object sender, MouseButtonEventArgs e) =>
        _lastTitleRenameBox = TerminalOf(sender) is not null && sender is TextBlock { Parent: Grid grid }
            ? grid.Children.OfType<TextBox>().FirstOrDefault()
            : null;

    private void BeginTerminalRename(LayoutContent content, TerminalPaneViewModel terminal)
    {
        terminal.BeginRename();

        // After the menu closed and the tab activation settled, or the terminal view would take the focus back.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            if (!terminal.IsRenaming)
            {
                return;
            }

            var box = _lastTitleRenameBox is { IsVisible: true, DataContext: LayoutContent shown } last && shown == content
                ? last
                : FindRenameBox(content);
            if (box is null)
            {
                terminal.CancelRename();
                return;
            }

            // An extension window (floating window) is activated only now; focusing first would lose the box to the
            // terminal view when the activation settles, which ends the edit at once.
            if (HostWindowOf(box) is { IsActive: false } window)
            {
                window.Activate();
                Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => FocusRenameBox(terminal, box));
                return;
            }

            FocusRenameBox(terminal, box);
        });
    }

    private static void FocusRenameBox(TerminalPaneViewModel terminal, TextBox box)
    {
        if (!terminal.IsRenaming || !box.IsVisible)
        {
            return;
        }

        Keyboard.Focus(box);
        box.SelectAll();
    }

    // The visible title edit box of a tab, in the main window or an extension window. Every presentation source is
    // searched: an extension window shows its panes in a hosted child source the window's own visual tree stops at.
    private static TextBox? FindRenameBox(LayoutContent content) =>
        PresentationSource.CurrentSources.OfType<PresentationSource>()
            .Select(s => s.RootVisual)
            .OfType<DependencyObject>()
            .SelectMany(Descendants)
            .OfType<TextBox>()
            .FirstOrDefault(b => b.Name == "RenameBox" && b.IsVisible && b.DataContext == content);

    // The window showing an element: its top-level native window. Window.GetWindow names the main window for an
    // extension window's panes (they are hosted in a child source owned by the docking manager).
    private static Window? HostWindowOf(Visual element)
    {
        if (PresentationSource.FromVisual(element) is not HwndSource source)
        {
            return Window.GetWindow(element);
        }

        var root = GetAncestor(source.Handle, GaRoot);
        return Application.Current.Windows.OfType<Window>().FirstOrDefault(w => new WindowInteropHelper(w).Handle == root)
            ?? Window.GetWindow(element);
    }

    private const uint GaRoot = 2;

    [LibraryImport("user32.dll")]
    private static partial nint GetAncestor(nint hwnd, uint flags);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }

    private void OnRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (TerminalOf(sender) is not { } terminal)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            terminal.CommitRename();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            terminal.CancelRename();
            e.Handled = true;
        }
    }

    private void OnRenameLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (TerminalOf(sender) is not { IsRenaming: true } terminal || sender is not TextBox box)
        {
            return;
        }

        // Decided once the focus change settled: focus moved elsewhere in the active window (another control, or the
        // terminal view, whose WebView2 leaves WPF focus empty) applies the name. The window losing activation keeps
        // the edit; WPF gives the box its focus back when the window is active again.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (terminal.IsRenaming && !box.IsKeyboardFocusWithin && HostWindowOf(box) is { IsActive: true })
            {
                terminal.CommitRename();
            }
        });
    }

    private void OnAboutClick(object sender, RoutedEventArgs e) =>
        MessageBox.Show(
            this,
            $"DotDeck {_shell.Version}\n개발 환경 설정 파일·문서 에디터\n\n홈: {_shell.Paths.Home}\n데이터: {_shell.Paths.LocalDataDir}",
            "DotDeck 정보",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
}
