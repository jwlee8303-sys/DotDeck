using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DotDeck.Core.Editors;

namespace DotDeck.App.ViewModels.Tools;

public sealed record ValidationRow(IssueSeverity Severity, string Message, int? Line)
{
    public string SeverityText => Severity switch
    {
        IssueSeverity.Error => "오류",
        IssueSeverity.Warning => "경고",
        _ => "정보",
    };

    public string Glyph => Severity switch
    {
        IssueSeverity.Error => "",
        IssueSeverity.Warning => "",
        _ => "",
    };

    public string LineText => Line is { } line ? $"{line}줄" : string.Empty;
}

/// <summary>Validation results of the active typed document (agents, skills, settings.json).</summary>
public sealed partial class ValidationPaneViewModel() : ToolPaneViewModel("tool.validation", "검증 결과", ToolLocation.Bottom)
{
    public ObservableCollection<ValidationRow> Issues { get; } = [];

    [ObservableProperty]
    private string _summary = "검증할 문서가 없습니다. agents·skills·settings.json 파일을 열면 표시됩니다.";

    /// <summary>Asks the active editor to move its caret to a line (set by the shell).</summary>
    public Action<int>? GoToLine { get; set; }

    public void Show(string fileName, IReadOnlyList<ValidationIssue> issues)
    {
        Issues.Clear();
        foreach (var issue in issues.OrderByDescending(i => i.Severity).ThenBy(i => i.Line ?? 0))
        {
            Issues.Add(new ValidationRow(issue.Severity, issue.Message, issue.Line));
        }

        var errors = issues.Count(i => i.Severity == IssueSeverity.Error);
        var warnings = issues.Count(i => i.Severity == IssueSeverity.Warning);
        Summary = issues.Count == 0 ? $"{fileName}: 문제 없음" : $"{fileName}: 오류 {errors}, 경고 {warnings}";
        Title = errors + warnings > 0 ? $"검증 결과 ({errors + warnings})" : "검증 결과";
    }

    public void Clear()
    {
        Issues.Clear();
        Summary = "검증할 문서가 없습니다. agents·skills·settings.json 파일을 열면 표시됩니다.";
        Title = "검증 결과";
    }
}
