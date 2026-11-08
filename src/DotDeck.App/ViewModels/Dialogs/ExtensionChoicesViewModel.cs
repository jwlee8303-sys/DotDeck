using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotDeck.Core.Explorer.Visibility;

namespace DotDeck.App.ViewModels.Dialogs;

/// <summary>One extension of the list with its check box.</summary>
public sealed partial class ExtensionChoice(string extension, bool isChecked) : ObservableObject
{
    public string Extension { get; } = extension;

    [ObservableProperty]
    private bool _isChecked = isChecked;
}

/// <summary>
/// The default-extension editor (SPEC 2.3): preset check list plus custom entries, "모든 파일 보기" and, for a
/// project, "전역 기본값 사용". Shared by the project dialog and the settings dialog.
/// </summary>
public sealed partial class ExtensionChoicesViewModel : ObservableObject
{
    public ExtensionChoicesViewModel(IReadOnlyList<string>? own, IReadOnlyList<string> globalDefault, bool showAllFiles, bool isProject)
    {
        IsProject = isProject;
        _useGlobalDefault = isProject && own is null;
        _showAllFiles = showAllFiles;
        Fill(own ?? globalDefault);
        GlobalDefault = globalDefault;
    }

    public bool IsProject { get; }

    public IReadOnlyList<string> GlobalDefault { get; }

    public ObservableCollection<ExtensionChoice> Choices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListEnabled))]
    private bool _useGlobalDefault;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListEnabled))]
    private bool _showAllFiles;

    [ObservableProperty]
    private string _customText = string.Empty;

    [ObservableProperty]
    private string? _error;

    /// <summary>The list is kept but disabled while all files are shown or the global default is used.</summary>
    public bool IsListEnabled => !ShowAllFiles && !UseGlobalDefault;

    partial void OnUseGlobalDefaultChanged(bool value)
    {
        if (value)
        {
            Fill(GlobalDefault);
        }
    }

    [RelayCommand]
    private void AddCustom()
    {
        Error = null;
        var added = new List<string>();
        foreach (var part in CustomText.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (ExtensionRules.Normalize(part) is not { } extension)
            {
                Error = $"확장자로 쓸 수 없습니다: {part}";
                return;
            }

            added.Add(extension);
        }

        foreach (var extension in added)
        {
            if (Choices.FirstOrDefault(c => c.Extension == extension) is { } existing)
            {
                existing.IsChecked = true;
            }
            else
            {
                Choices.Add(new ExtensionChoice(extension, true));
            }
        }

        CustomText = string.Empty;
    }

    [RelayCommand]
    private void ApplyPreset()
    {
        ShowAllFiles = false;
        foreach (var choice in Choices)
        {
            choice.IsChecked = ExtensionRules.ConfigDocsPreset.Contains(choice.Extension);
        }
    }

    /// <summary>The checked extensions (normalized, sorted).</summary>
    public IReadOnlyList<string> Checked => ExtensionRules.NormalizeAll(Choices.Where(c => c.IsChecked).Select(c => c.Extension));

    /// <summary>The project's own list, or null for "전역 기본값 사용".</summary>
    public IReadOnlyList<string>? OwnList => UseGlobalDefault ? null : Checked;

    private void Fill(IReadOnlyList<string> checkedExtensions)
    {
        var set = new HashSet<string>(ExtensionRules.NormalizeAll(checkedExtensions), StringComparer.Ordinal);
        var all = ExtensionRules.ConfigDocsPreset.Concat(set).Concat(Choices.Select(c => c.Extension)).Distinct(StringComparer.Ordinal).ToList();
        Choices.Clear();
        foreach (var extension in all)
        {
            Choices.Add(new ExtensionChoice(extension, set.Contains(extension)));
        }
    }
}
