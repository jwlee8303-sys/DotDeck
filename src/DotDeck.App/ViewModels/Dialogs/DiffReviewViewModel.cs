using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotDeck.Core.Documents;

namespace DotDeck.App.ViewModels.Dialogs;

/// <summary>A button of a review dialog. <paramref name="Id"/> is returned as the dialog result.</summary>
public sealed record DialogChoice(string Id, string Label, bool IsDefault = false, bool IsCancel = false);

/// <summary>One file in a review dialog: its name, path, change summary and compacted diff.</summary>
public sealed partial class DiffReviewItem : ObservableObject
{
    private readonly IReadOnlyList<DiffLine> _raw;
    public DiffReviewItem(string path, TextDiff diff, string? note = null)
    {
        Path = path;
        _raw = diff.Compact();
        Summary = $"+{diff.InsertedCount} −{diff.DeletedCount}" + (note is null ? string.Empty : $" · {note}");
    }

    public string Path { get; }

    public string Name => System.IO.Path.GetFileName(Path);

    public string Summary { get; }

    /// <summary>Screen share mode hides the whole diff body until the eye button is pressed.</summary>
    public bool CanReveal { get; } = DotDeck.Core.Security.SecretPresentation.ScreenShareMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Lines))]
    private bool _isRevealed = !DotDeck.Core.Security.SecretPresentation.ScreenShareMode;
    public IReadOnlyList<DiffLine> Lines => IsRevealed ? _raw : [.. _raw.Select(l => l.Kind == DiffLineKind.Gap ? l : l with { Text = DotDeck.Core.Security.SecretMasker.Mask })];
    [RelayCommand]
    private void Reveal() => IsRevealed = !IsRevealed;
}

/// <summary>
/// Shows diffs and asks for a decision: save preview, save all, external change (reload / overwrite / cancel).
/// <see cref="Result"/> is the chosen <see cref="DialogChoice.Id"/>, or null when closed otherwise.
/// </summary>
public sealed partial class DiffReviewViewModel : ObservableObject
{
    public DiffReviewViewModel(string title, string message, IEnumerable<DiffReviewItem> items, IReadOnlyList<DialogChoice> choices)
    {
        Title = title;
        Message = message;
        Items = [.. items];
        Choices = choices;
        _selectedItem = Items.FirstOrDefault();
    }

    public string Title { get; }

    public string Message { get; }

    public ObservableCollection<DiffReviewItem> Items { get; }

    public bool ShowsFileList => Items.Count > 1;

    public IReadOnlyList<DialogChoice> Choices { get; }

    /// <summary>Text of the optional "don't ask again" box; null hides it.</summary>
    public string? SkipLabel { get; init; }

    [ObservableProperty]
    private DiffReviewItem? _selectedItem;

    [ObservableProperty]
    private bool _skipNextTime;

    public string? Result { get; private set; }

    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void Choose(DialogChoice? choice)
    {
        Result = choice?.Id;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
