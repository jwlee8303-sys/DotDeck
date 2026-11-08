using System.Globalization;
using System.Windows;
using System.Windows.Data;
using DotDeck.Core.Explorer;

namespace DotDeck.App.Converters;

/// <summary>Tree depth → left margin.</summary>
public sealed class DepthToIndentConverter : IValueConverter
{
    public double Step { get; set; } = 16;

    public double Offset { get; set; } = 4;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new Thickness(Offset + (value is int depth && depth > 0 ? depth * Step : 0), 0, 0, 0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>bool → !bool (both directions).</summary>
public sealed class InvertBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>false → Visible, true → Collapsed.</summary>
public sealed class FalseToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>null → Collapsed, anything else → Visible.</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>File kind → Segoe Fluent Icons glyph (colors are set by styles so themes can change them).</summary>
public sealed class FileKindToGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        FileKind.Markdown or FileKind.Text => "",
        FileKind.PowerShell or FileKind.Script => "",
        FileKind.DotConfig => "",
        FileKind.KeyMaterial => "",
        FileKind.Other => "",
        _ => "",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
