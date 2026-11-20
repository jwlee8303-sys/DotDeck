using System.Globalization;
using System.Windows;
using System.Windows.Data;
using DotDeck.App.Themes;
using DotDeck.App.ViewModels.Documents;
using DotDeck.Core.Explorer;

namespace DotDeck.App.Converters;

/// <summary>
/// [item, isExpanded?, FileIcons.Version] → the item's colored 16×16 icon. The item is a <see cref="NodeInfo"/> (tree),
/// a <see cref="TextDocumentViewModel"/> (tabs; other documents get none) or a file path. The version value is unused:
/// it only makes the binding re-evaluate when icons change.
/// </summary>
public sealed class FileIconConverter : IMultiValueConverter
{
    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var expanded = values.Length > 1 && values[1] is true;
        return values.Length > 0 ? values[0] switch
        {
            NodeInfo node => FileIcons.ImageFor(node.Name, node.FullPath, node.Kind == NodeKind.Folder, expanded),
            TextDocumentViewModel document => FileIcons.ImageFor(System.IO.Path.GetFileName(document.FilePath), document.FilePath, isFolder: false, isExpanded: false),
            string { Length: > 0 } path => FileIcons.ImageFor(System.IO.Path.GetFileName(path), path, isFolder: false, isExpanded: false),
            _ => null,
        } : null;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Document tab content → Visible for file tabs (the icon slot), Collapsed for the start page, terminals, ….</summary>
public sealed class FileTabToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is TextDocumentViewModel ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>[file path, FileIcons.DirtyVersion] → Visible while the file is open with unsaved changes.</summary>
public sealed class DirtyFileToVisibleConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length > 0 && FileIcons.IsDirty(values[0] as string) ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
