using System.Collections;
using System.Windows;
using System.Windows.Controls;

namespace DotDeck.App.Views.Controls;

/// <summary>Read-only unified diff list (old/new line numbers, +/- marker, coloured rows).</summary>
public partial class DiffView : UserControl
{
    public static readonly DependencyProperty LinesProperty =
        DependencyProperty.Register(nameof(Lines), typeof(IEnumerable), typeof(DiffView));

    public DiffView() => InitializeComponent();

    public IEnumerable? Lines
    {
        get => (IEnumerable?)GetValue(LinesProperty);
        set => SetValue(LinesProperty, value);
    }
}
