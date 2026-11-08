using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotDeck.Core.Toml;
using Tomlyn.Syntax;

namespace DotDeck.App.ViewModels.Editor;

public sealed record AdvancedTomlNode(string Label, IReadOnlyList<string> Path, string? Literal = null, bool IsSecret = false)
{
    public ObservableCollection<AdvancedTomlNode> Children { get; } = [];
}

public sealed partial class AdvancedTomlViewModel(Func<TomlEditDocument?> document, Action<Action<TomlEditDocument>> edit) : ObservableObject
{
    public ObservableCollection<AdvancedTomlNode> Roots { get; } = [];
    public StringListViewModel Array { get; } = new();
    public KeyValueTableViewModel Inline { get; } = new();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit), nameof(IsSecret), nameof(IsString), nameof(IsBoolean), nameof(IsArray), nameof(IsInline), nameof(IsRaw))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(DeleteCommand))]
    private AdvancedTomlNode? _selected;
    [ObservableProperty] private string _scalar = string.Empty;
    [ObservableProperty] private bool _boolean;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DirectAddCommand), nameof(DirectTableCommand))]
    private string _directPath = string.Empty;
    [ObservableProperty] private string _directLiteral = string.Empty;
    private ValueSyntax? _syntax;
    public bool CanEdit => Selected?.Literal is not null;
    public bool IsSecret => Selected?.IsSecret == true;
    public bool IsString => CanEdit && _syntax is StringValueSyntax;
    public bool IsBoolean => CanEdit && _syntax is BooleanValueSyntax;
    public bool IsArray => CanEdit && _syntax is ArraySyntax;
    public bool IsInline => CanEdit && _syntax is InlineTableSyntax;
    public bool IsRaw => CanEdit && !IsString && !IsBoolean && !IsArray && !IsInline;
    public string TypeLabel => IsString ? "문자열" : IsBoolean ? "불리언" : IsArray ? "배열 항목 (각 줄은 TOML 값)" : IsInline ? "인라인 테이블 (각 값은 TOML 형식)" : "숫자·날짜·기타 TOML 값";

    private static bool Known(TomlEditDocument.Entry e)
    {
        var p = e.Path;
        var complex = e.Literal.StartsWith('{') || e.Literal.StartsWith('[');
        string[] fields = ["model", "model_reasoning_effort", "plan_mode_reasoning_effort", "approval_policy", "sandbox_mode"];
        if (p.Count == 1 && fields.Contains(p[0])) return !complex;
        if (p.Count == 3 && p[0] == "profiles" && fields.Contains(p[2])) return !complex;
        if (p.Count >= 3 && p[0] == "mcp_servers" && p[2] is "command" or "args" or "enabled" or "env") return true;
        return p.Count == 3 && p[0] == "projects" && p[2] == "trust_level" && !complex;
    }
    private static bool Prefix(IReadOnlyList<string> prefix, IReadOnlyList<string> path) => prefix.Count < path.Count && prefix.SequenceEqual(path.Take(prefix.Count));

    public void Load()
    {
        var selected = Selected?.Path;
        Roots.Clear();
        var root = new AdvancedTomlNode("루트", []); Roots.Add(root);
        var entries = document()?.Entries ?? [];
        foreach (var e in entries)
        {
            if (Known(e) || entries.Any(parent => Prefix(parent.Path, e.Path) && (parent.Literal.StartsWith('[') || parent.Literal.StartsWith('{')))) continue;
            var parent = root;
            for (var i = 0; i < e.Path.Count - 1; i++)
            {
                var group = parent.Children.FirstOrDefault(n => n.Label == e.Path[i] && n.Literal is null);
                if (group is null) { group = new AdvancedTomlNode(e.Path[i], e.Path.Take(i + 1).ToArray()); parent.Children.Add(group); }
                parent = group;
            }
            parent.Children.Add(new AdvancedTomlNode(e.Path[^1], e.Path, e.Literal,
                Core.Security.SecretPresentation.SensitivePath(e.Path) || entries.Any(child => Prefix(e.Path, child.Path) && Core.Security.SecretPresentation.SensitivePath(child.Path))));
        }
        Selected = selected is null ? null : Flatten(root).FirstOrDefault(n => n.Path.SequenceEqual(selected) && n.Literal is not null);
    }
    private static IEnumerable<AdvancedTomlNode> Flatten(AdvancedTomlNode node) => new[] { node }.Concat(node.Children.SelectMany(Flatten));
    partial void OnSelectedChanged(AdvancedTomlNode? value)
    {
        var source = "v = " + (value?.Literal ?? "0");
        _syntax = Tomlyn.Parsing.SyntaxParser.Parse(source).KeyValues.First().Value;
        Scalar = value?.Literal is { } literal ? IsString ? CodexConfigFormViewModel.Decode(literal) : literal : string.Empty;
        Boolean = _syntax is BooleanValueSyntax b && b.Value;
        Array.Load(_syntax is ArraySyntax a ? a.Items.Select(item => source.Substring(item.Value!.Span.Start.Offset, item.Value.Span.Length)) : []);
        Inline.Load(_syntax is InlineTableSyntax table ? table.Items.Select(item => KeyValuePair.Create(
            source.Substring(item.KeyValue!.Key!.Span.Start.Offset, item.KeyValue.Key.Span.Length),
            source.Substring(item.KeyValue.Value!.Span.Start.Offset, item.KeyValue.Value.Span.Length))) : []);
        foreach (var row in Inline.Rows) row.ForceSecret = IsSecret;
        OnPropertyChanged(nameof(TypeLabel));
    }
    private bool CanDelete() => Selected is { } selected && document()?.CanDeleteKey(selected.Path) == true;
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Apply()
    {
        if (Selected is not { Literal: not null } selected) return;
        edit(d =>
        {
            if (IsString) d.SetString(selected.Path, Scalar);
            else if (IsBoolean) d.SetValue(selected.Path, Boolean ? "true" : "false");
            else if (IsArray) d.SetArrayLiterals(selected.Path, Array.Values);
            else if (IsInline) d.SetInlineValues(selected.Path, Inline.Values().Where(p => p.Key.Length > 0).ToList());
            else d.SetValue(selected.Path, Scalar);
        });
    }
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete() { if (Selected is { } selected) edit(d => d.DeleteKey(selected.Path)); }
    private bool CanPath() { try { _ = TomlEditDocument.ParsePath(DirectPath); return true; } catch (TomlEditException) { return false; } }
    private bool CanAdd() => CanPath() && document()?.CanSetValue(TomlEditDocument.ParsePath(DirectPath)) == true;
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void DirectAdd() { if (CanAdd()) edit(d => d.SetValue(TomlEditDocument.ParsePath(DirectPath), DirectLiteral)); }
    [RelayCommand(CanExecute = nameof(CanPath))]
    private void DirectTable() { if (CanPath()) edit(d => d.CreateTable(TomlEditDocument.ParsePath(DirectPath))); }
}
