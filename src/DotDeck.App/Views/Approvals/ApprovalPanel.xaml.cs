using System.Windows;
using System.Windows.Controls;

namespace DotDeck.App.Views.Approvals;

/// <summary>
/// Approval area bound to an <c>ApprovalItemViewModel</c>. Where it is shown decides the extra buttons: AI 모니터
/// ([터미널로 이동]) or a notification card ([열기]).
/// </summary>
public partial class ApprovalPanel : UserControl
{
    public static readonly DependencyProperty ShowGoToTerminalProperty =
        DependencyProperty.Register(nameof(ShowGoToTerminal), typeof(bool), typeof(ApprovalPanel), new PropertyMetadata(false));

    public static readonly DependencyProperty ShowOpenProperty =
        DependencyProperty.Register(nameof(ShowOpen), typeof(bool), typeof(ApprovalPanel), new PropertyMetadata(false));

    public ApprovalPanel()
    {
        InitializeComponent();
    }

    public bool ShowGoToTerminal
    {
        get => (bool)GetValue(ShowGoToTerminalProperty);
        set => SetValue(ShowGoToTerminalProperty, value);
    }

    public bool ShowOpen
    {
        get => (bool)GetValue(ShowOpenProperty);
        set => SetValue(ShowOpenProperty, value);
    }
}
