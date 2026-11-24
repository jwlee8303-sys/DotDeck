using System.Globalization;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DotDeck.App.ViewModels.Approvals;

namespace DotDeck.App.Views.Approvals;

/// <summary>
/// Notification cards at the bottom right of the primary monitor's work area (above the taskbar). The window never
/// takes the focus from other applications (<c>ShowActivated=false</c>, <c>WS_EX_NOACTIVATE</c>) and is not in the
/// taskbar or Alt+Tab (<c>WS_EX_TOOLWINDOW</c>). Colors follow the DotDeck theme resources.
/// </summary>
public partial class ApprovalCardsWindow : Window
{
    public const int MaxCards = 3;
    private const int Gap = 12;
    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000L;
    private const long WsExToolWindow = 0x00000080L;
    private string? _overflowKey;
    private Action<string>? _open;

    public ApprovalCardsWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
            SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style | WsExNoActivate | WsExToolWindow));
        };
        SizeChanged += (_, _) => Place();
    }

    /// <summary>Shows <paramref name="items"/> (oldest first): the oldest card at the bottom, the rest folded into one line.</summary>
    public void ShowCards(IReadOnlyList<ApprovalItemViewModel> items, Action<string> open)
    {
        ArgumentNullException.ThrowIfNull(items);
        _open = open;
        var shown = items.Take(MaxCards).Reverse().ToList();
        if (Cards.ItemsSource is not IList<ApprovalItemViewModel> current || !current.SequenceEqual(shown))
        {
            Cards.ItemsSource = shown;
        }

        var more = items.Count - MaxCards;
        Overflow.Visibility = more > 0 ? Visibility.Visible : Visibility.Collapsed;
        OverflowButton.Content = string.Create(CultureInfo.InvariantCulture, $"외 {more}건 — AI 모니터에서 보기");
        _overflowKey = more > 0 ? items[MaxCards].SessionKey : null;
        if (!IsVisible)
        {
            Show();
        }

        Place();
    }

    public static void PlaySound() => SystemSounds.Asterisk.Play();

    private void Place()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - Gap;
        Top = area.Bottom - ActualHeight - Gap;
    }

    private void OnOverflowClick(object sender, RoutedEventArgs e)
    {
        if (_overflowKey is { } key)
        {
            _open?.Invoke(key);
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}

/// <summary>Shows or hides the cards after every approval update.</summary>
public sealed class ApprovalCardsController
{
    private readonly ApprovalCoordinator _approvals;
    private ApprovalCardsWindow? _window;

    public ApprovalCardsController(ApprovalCoordinator approvals)
    {
        _approvals = approvals ?? throw new ArgumentNullException(nameof(approvals));
        approvals.Updated += (_, _) => Update();
        approvals.CardAdded += (_, _) =>
        {
            if (ShouldShow && _approvals.Service.Settings.NotificationSound)
            {
                ApprovalCardsWindow.PlaySound();
            }
        };
    }

    private bool ShouldShow => _approvals.Service.Settings is { Enabled: true, NotificationCards: true } && !_approvals.IsAppActive;

    private void Update()
    {
        var items = ShouldShow ? _approvals.CardItems : [];
        if (items.Count == 0)
        {
            _window?.Hide();
            return;
        }

        _window ??= new ApprovalCardsWindow();
        _window.ShowCards(items, _approvals.Open);
    }

    public void Close() => _window?.Close();
}
