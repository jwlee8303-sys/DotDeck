using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotDeck.Core.Documents;
using ICSharpCode.AvalonEdit.Document;

namespace DotDeck.App.ViewModels.Editor;

/// <summary>Selection access the editor view provides to view models (keeps the TextEditor control out of them).</summary>
public interface IEditorSelection
{
    int SelectionStart { get; }

    int SelectionLength { get; }

    int CaretOffset { get; }

    void Select(int start, int length);

    void FocusEditor();
}

/// <summary>Find/replace bar of one editor tab. Works on the shared <see cref="TextDocument"/>.</summary>
public sealed partial class FindReplaceViewModel : ObservableObject
{
    private readonly TextDocument _document;

    public FindReplaceViewModel(TextDocument document) => _document = document;

    public Func<bool> CanEdit { get; init; } = () => true;

    /// <summary>Set by the view while it is loaded.</summary>
    public IEditorSelection? Editor { get; set; }

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private bool _isReplaceVisible;

    [ObservableProperty]
    private string _pattern = string.Empty;

    [ObservableProperty]
    private string _replacement = string.Empty;

    [ObservableProperty]
    private bool _matchCase;

    [ObservableProperty]
    private bool _wholeWord;

    [ObservableProperty]
    private bool _useRegex;

    [ObservableProperty]
    private string? _message;

    /// <summary>Raised when the bar should take keyboard focus (opened with Ctrl+F/H).</summary>
    public event EventHandler? FocusRequested;

    public void Open(bool replace)
    {
        if (Editor is { SelectionLength: > 0 and < 200 } editor)
        {
            var selected = _document.GetText(editor.SelectionStart, editor.SelectionLength);
            if (!selected.Contains('\n', StringComparison.Ordinal))
            {
                Pattern = selected;
            }
        }

        IsReplaceVisible = replace;
        IsOpen = true;
        Message = null;
        FocusRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Close()
    {
        IsOpen = false;
        Editor?.FocusEditor();
    }

    [RelayCommand]
    private void FindNext() => Guard(() => Find(backward: false));

    [RelayCommand]
    private void FindPrevious() => Guard(() => Find(backward: true));

    [RelayCommand]
    private void ReplaceOne() { if (CanEdit()) Guard(ReplaceOneCore); }

    [RelayCommand]
    private void ReplaceAll() { if (CanEdit()) Guard(ReplaceAllCore); }

    // Regex evaluation has a timeout (catastrophic backtracking); report it instead of crashing.
    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            Message = "검색 시간이 초과되었습니다. 정규식을 단순하게 바꿔 보세요.";
        }
    }

    private void ReplaceOneCore()
    {
        if (Editor is not { } editor || Build() is not { } regex)
        {
            return;
        }

        var text = _document.Text;
        var current = new TextMatch(editor.SelectionStart, editor.SelectionLength);
        if (current.Length > 0 && TextSearch.ExpandReplacement(text, regex, current, Replacement, UseRegex) is { } value)
        {
            _document.Replace(current.Start, current.Length, value);
            editor.Select(current.Start + value.Length, 0);
        }

        Find(backward: false);
    }

    private void ReplaceAllCore()
    {
        if (Build() is not { } regex)
        {
            return;
        }

        var text = _document.Text;
        var matches = TextSearch.FindAll(text, regex);
        if (matches.Count == 0)
        {
            Message = "찾을 수 없습니다.";
            return;
        }

        // Replace back to front inside one undo group so offsets stay valid and Ctrl+Z reverts everything.
        _document.BeginUpdate();
        try
        {
            for (var i = matches.Count - 1; i >= 0; i--)
            {
                var m = matches[i];
                var value = TextSearch.ExpandReplacement(text, regex, m, Replacement, UseRegex) ?? Replacement;
                _document.Replace(m.Start, m.Length, value);
            }
        }
        finally
        {
            _document.EndUpdate();
        }

        Message = $"{matches.Count}개 바꿈";
    }

    private void Find(bool backward)
    {
        if (Editor is not { } editor || Build() is not { } regex)
        {
            return;
        }

        var from = backward ? editor.SelectionStart : editor.SelectionStart + editor.SelectionLength;
        var text = _document.Text;
        if (TextSearch.FindNext(text, regex, from, backward) is { } match)
        {
            editor.Select(match.Start, match.Length);
            var total = TextSearch.FindAll(text, regex).Count;
            Message = total == 1 ? "1개" : $"{total}개";
        }
        else
        {
            Message = "찾을 수 없습니다.";
        }
    }

    private System.Text.RegularExpressions.Regex? Build()
    {
        var regex = TextSearch.TryBuild(new SearchOptions(Pattern, MatchCase, WholeWord, UseRegex), out var error);
        Message = error;
        return regex;
    }
}
