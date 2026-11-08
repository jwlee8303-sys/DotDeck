using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using DotDeck.App.Infrastructure.Windows;
using Microsoft.Win32;

namespace DotDeck.App.Views.Dialogs;

public sealed partial class BackupChoice(string id, string name, string root) : ObservableObject
{
    public string Id { get; } = id;

    public string Name { get; } = name;

    public string Root { get; } = root;

    [ObservableProperty]
    private bool _isSelected = true;
}

public partial class ProjectBackupDialog : Window
{
    private readonly IReadOnlyList<BackupChoice> _choices;

    public ProjectBackupDialog(IReadOnlyList<BackupChoice> choices, string? destination)
    {
        InitializeComponent();
        DarkTitleBar.Attach(this);
        _choices = choices;
        ProjectList.ItemsSource = choices;
        DestinationBox.Text = destination ?? string.Empty;
    }

    public IReadOnlyList<string> SelectedIds { get; private set; } = [];

    public string Destination { get; private set; } = string.Empty;

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        var select = !_choices.All(c => c.IsSelected);
        foreach (var choice in _choices)
        {
            choice.IsSelected = select;
        }
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "백업을 저장할 폴더" };
        if (Directory.Exists(DestinationBox.Text))
        {
            dialog.InitialDirectory = DestinationBox.Text;
        }

        if (dialog.ShowDialog(this) == true)
        {
            DestinationBox.Text = dialog.FolderName;
        }
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var selected = _choices.Where(c => c.IsSelected).Select(c => c.Id).ToList();
        if (selected.Count == 0)
        {
            ShowError("백업할 프로젝트를 하나 이상 선택하세요.");
            return;
        }

        var destination = DestinationBox.Text.Trim();
        if (!Path.IsPathFullyQualified(destination))
        {
            ShowError("저장할 폴더를 전체 경로로 지정하세요.");
            return;
        }

        SelectedIds = selected;
        Destination = destination;
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
