using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DotDeck.App.Converters;

/// <summary>
/// bool ↔ Visibility using <see cref="Visibility.Hidden"/> for false, which AvalonDock interprets as
/// "hide the anchorable" (Collapsed would remove it from the layout).
/// </summary>
public sealed class BoolToHiddenConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Hidden;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}
