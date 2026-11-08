using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotDeck.Core.Codex;
using DotDeck.Core.Toml;

namespace DotDeck.App.ViewModels.Editor;

public sealed partial class CodexFieldViewModel : ObservableObject
{
    private readonly Action<string?> _change;
    public ConfigField Metadata { get; }
    public string Label => Metadata.Label;
    public string Description => Metadata.Description;
    public string Default => "기본값: " + Metadata.Default;
    public IReadOnlyList<ConfigChoice> Choices => Metadata.Choices;
    public bool IsComplex { get; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Warning))]
    private string _value;
    public string? Warning => IsComplex ? "복합 값은 기타(고급)에서 편집합니다. 기존 값은 보존됩니다."
        : Value.Length > 0 && Choices.Count > 0 && Choices.All(c => c.Value != Value) ? "카탈로그에 없는 값입니다. 그대로 보존합니다." : null;
    public CodexFieldViewModel(ConfigField metadata, string value, bool complex, Action<string?> change)
    { Metadata = metadata; _value = value; IsComplex = complex; _change = change; }
    partial void OnValueChanged(string value) { if (!IsComplex) _change(value); }
    [RelayCommand]
    private void Reset() => _change(null);
}

public sealed partial class CodexServerViewModel : ObservableObject
{
    private readonly Action<string, string?> _field;
    private bool _loading;
    public string Name { get; }
    public StringListViewModel Args { get; }
    public KeyValueTableViewModel Env { get; }
    [ObservableProperty] private string _command;
    [ObservableProperty] private bool? _enabled;
    public CodexServerViewModel(string name, string command, bool? enabled, IEnumerable<string> args,
        IEnumerable<KeyValuePair<string, string>> env, Action<string, string?> field, Action argsChanged, Action envChanged)
    {
        _loading = true; Name = name; _command = command; _enabled = enabled; _field = field;
        Args = new StringListViewModel { Changed = argsChanged }; Args.Load(args);
        Env = new KeyValueTableViewModel { Changed = envChanged }; Env.Load(env);
        _loading = false;
    }
    partial void OnCommandChanged(string value) { if (!_loading) _field("command", value); }
    partial void OnEnabledChanged(bool? value) { if (!_loading) _field("enabled", value?.ToString().ToLowerInvariant()); }
}

/// <summary>Dedicated config form. All writes go through the lossless TOML document into one shared undo buffer.</summary>
public sealed partial class CodexConfigFormViewModel : DocumentFormViewModel
{
    private TomlEditDocument? _doc;
    private bool _refreshing;
    private readonly ConfigFormCatalog _catalog;
    public Func<string?>? PickFolder { get; init; }
    public ObservableCollection<CodexFieldViewModel> Models { get; } = [];
    public ObservableCollection<CodexFieldViewModel> Permissions { get; } = [];
    public ObservableCollection<string> Profiles { get; } = [];
    public ObservableCollection<CodexFieldViewModel> ProfileFields { get; } = [];
    public ObservableCollection<CodexServerViewModel> Servers { get; } = [];
    public ObservableCollection<string> Projects { get; } = [];
    public ObservableCollection<CodexFieldViewModel> TrustFields { get; } = [];
    [ObservableProperty] private string? _selectedProfile;
    [ObservableProperty] private CodexServerViewModel? _selectedServer;
    [ObservableProperty] private string? _selectedProject;
    [ObservableProperty] private string _profileName = string.Empty;
    [ObservableProperty] private string _serverName = string.Empty;
    [ObservableProperty] private string _projectPath = string.Empty;
    [ObservableProperty] private string? _message;
    public const string ProfileNotice = "기존 [profiles.*] 형식입니다. 최신 Codex는 별도 <이름>.config.toml을 사용합니다. 자동 변환하지 않습니다.";
    public CodexConfigFormViewModel(Action<string> apply, ConfigFormCatalog? catalog = null) : base(apply) => _catalog = catalog ?? ConfigFormCatalog.BuiltIn;

    protected override string? LoadCore(string text)
    {
        try { _doc = TomlEditDocument.Parse(Encoding.UTF8.GetBytes(text)); Refresh(); return null; }
        catch (TomlEditException ex) { return $"TOML 오류: {ex.Line}행 {ex.Column}열"; }
    }

    internal static string Decode(string literal)
    {
        var node = Tomlyn.Parsing.SyntaxParser.Parse("v = " + literal).KeyValues.First().Value;
        return node is Tomlyn.Syntax.StringValueSyntax text ? text.Value ?? string.Empty : literal;
    }
    private TomlEditDocument.Entry? Find(IReadOnlyList<string> path) => _doc?.Entries.FirstOrDefault(e => e.Path.SequenceEqual(path));
    private IEnumerable<string> Names(string section) => (_doc?.TablePaths ?? []).Concat(_doc?.Entries.Select(e => e.Path) ?? [])
        .Where(p => p.Count >= 2 && p[0] == section).Select(p => p[1]).Distinct(StringComparer.Ordinal);
    private void Fields(ObservableCollection<CodexFieldViewModel> list, IReadOnlyList<string> prefix, IEnumerable<string> keys)
    {
        list.Clear();
        foreach (var key in keys)
        {
            var metadata = _catalog.Fields.FirstOrDefault(f => f.Key == key);
            if (metadata is null) continue;
            var path = prefix.Append(key).ToArray();
            var entry = Find(path);
            var complex = entry?.Literal.StartsWith('{') == true || entry?.Literal.StartsWith('[') == true;
            list.Add(new CodexFieldViewModel(metadata, entry is null ? string.Empty : Decode(entry.Literal), complex, value =>
                Edit(d => { if (value is null) { if (d.CanDeleteKey(path)) d.DeleteKey(path); } else { Ensure(d, prefix); d.SetString(path, value); } }, value is null)));
        }
    }
    private void Refresh()
    {
        _refreshing = true;
        try
        {
            Fields(Models, [], ["model", "model_reasoning_effort", "plan_mode_reasoning_effort"]);
            Fields(Permissions, [], ["approval_policy", "sandbox_mode"]);
            var profile = SelectedProfile; Profiles.Clear(); foreach (var n in Names("profiles")) Profiles.Add(n);
            SelectedProfile = profile is not null && Profiles.Contains(profile) ? profile : Profiles.FirstOrDefault();
            var project = SelectedProject; Projects.Clear(); foreach (var n in Names("projects")) Projects.Add(n);
            SelectedProject = project is not null && Projects.Contains(project) ? project : Projects.FirstOrDefault();
            var selected = SelectedServer?.Name;
            Servers.Clear();
            foreach (var name in Names("mcp_servers"))
            {
                string[] prefix = ["mcp_servers", name];
                var args = _doc!.Entries.Where(e => e.Path.Count == 4 && e.Path.Take(3).SequenceEqual(prefix.Append("args"))).Select(e => Decode(e.Literal)).ToList();
                var env = _doc.Entries.Where(e => e.Path.Count == 4 && e.Path.Take(3).SequenceEqual(prefix.Append("env"))).Select(e => KeyValuePair.Create(e.Path[3], Decode(e.Literal))).ToList();
                CodexServerViewModel? server = null;
                server = new CodexServerViewModel(name, Find(prefix.Append("command").ToArray()) is { } command ? Decode(command.Literal) : string.Empty,
                    Find(prefix.Append("enabled").ToArray()) is { } enabled && bool.TryParse(enabled.Literal, out var b) ? b : null, args, env,
                    (key, value) => Edit(d => { Ensure(d, prefix); var path = prefix.Append(key).ToArray(); if (value is null) { if (d.CanDeleteKey(path)) d.DeleteKey(path); }
                        else if (key == "enabled") d.SetValue(path, value); else d.SetString(path, value); }, false),
                    () => Edit(d => { Ensure(d, prefix); d.SetStringList([.. prefix, "args"], server!.Args.Values); }, false),
                    () => SaveEnv(prefix, server!.Env.Values()));
                Servers.Add(server);
            }
            SelectedServer = Servers.FirstOrDefault(s => s.Name == selected) ?? Servers.FirstOrDefault();
        }
        finally { _refreshing = false; }
        ProfileFieldsChanged(); ProjectFieldsChanged();
    }
    private static void Ensure(TomlEditDocument doc, IReadOnlyList<string> prefix)
    {
        if (prefix.Count > 0 && !doc.CanSetValue(prefix.Append("__probe__").ToArray())) doc.CreateTable(prefix);
    }
    private void SaveEnv(string[] prefix, IReadOnlyList<KeyValuePair<string, string>> values)
    {
        Edit(d =>
        {
            string[] envPath = [.. prefix, "env"];
            if (Find(envPath) is not null)
            {
                var existing = d.Entries.Where(e => e.Path.Count == 4 && e.Path.Take(3).SequenceEqual(envPath)).ToDictionary(e => e.Path[3]);
                if (values.Count == existing.Count && values.All(p => existing.ContainsKey(p.Key)))
                {
                    foreach (var pair in values)
                        if (Decode(existing[pair.Key].Literal) != pair.Value) d.SetString([.. envPath, pair.Key], pair.Value);
                    return;
                }
                d.SetValue(envPath, "{ " + string.Join(", ", values.Where(p => p.Key.Length > 0).Select(p =>
                    TomlEditDocument.FormatPath([p.Key]) + " = " + (existing.TryGetValue(p.Key, out var previous) && Decode(previous.Literal) == p.Value
                        ? previous.Literal : JsonSerializer.Serialize(p.Value)))) + " }");
                return;
            }
            Ensure(d, envPath);
            foreach (var old in d.Entries.Where(e => e.Path.Count == 4 && e.Path.Take(3).SequenceEqual(envPath)).ToArray())
                if (values.All(p => p.Key != old.Path[3]) && d.CanDeleteKey(old.Path)) d.DeleteKey(old.Path);
            foreach (var pair in values.Where(p => p.Key.Length > 0))
            {
                var path = envPath.Append(pair.Key).ToArray();
                if (Find(path) is not { } previous || Decode(previous.Literal) != pair.Value) d.SetString(path, pair.Value);
            }
        }, false);
    }
    partial void OnSelectedProfileChanged(string? value) { if (!_refreshing) ProfileFieldsChanged(); }
    partial void OnSelectedProjectChanged(string? value) { if (!_refreshing) ProjectFieldsChanged(); }
    private void ProfileFieldsChanged()
    { ProfileFields.Clear(); if (SelectedProfile is { } name) Fields(ProfileFields, ["profiles", name], ["model", "model_reasoning_effort", "plan_mode_reasoning_effort", "approval_policy", "sandbox_mode"]); }
    private void ProjectFieldsChanged()
    { TrustFields.Clear(); if (SelectedProject is { } path) Fields(TrustFields, ["projects", path], ["trust_level"]); }
    [RelayCommand] private void AddProfile() { if (ProfileName.Length > 0) Edit(d => d.CreateTable(["profiles", ProfileName])); }
    [RelayCommand] private void DeleteProfile() { if (SelectedProfile is { } name) Edit(d => d.DeleteTable(["profiles", name])); }
    [RelayCommand] private void RenameProfile() { if (SelectedProfile is { } name && ProfileName.Length > 0) Edit(d => d.RenameTable(["profiles", name], ProfileName)); }
    [RelayCommand] private void AddServer() { if (ServerName.Length > 0) Edit(d => d.CreateTable(["mcp_servers", ServerName])); }
    [RelayCommand] private void DeleteServer() { if (SelectedServer is { } server) Edit(d => d.DeleteTable(["mcp_servers", server.Name])); }
    [RelayCommand] private void RenameServer() { if (SelectedServer is { } server && ServerName.Length > 0) Edit(d => d.RenameTable(["mcp_servers", server.Name], ServerName)); }
    [RelayCommand] private void AddProject() { if (ProjectPath.Length > 0) Edit(d => { Ensure(d, ["projects", ProjectPath]); d.SetString(["projects", ProjectPath, "trust_level"], "trusted"); }); }
    [RelayCommand] private void DeleteProject() { if (SelectedProject is { } path) Edit(d => d.DeleteTable(["projects", path])); }
    [RelayCommand] private void BrowseProject() { if (PickFolder?.Invoke() is { } path) ProjectPath = path; }

    private void Edit(Action<TomlEditDocument> edit, bool refresh = true)
    {
        if (_refreshing || _doc is null) return;
        try
        {
            var next = TomlEditDocument.Parse(_doc.ToBytes()); edit(next); _doc = next;
            if (refresh) Refresh();
            var text = Encoding.UTF8.GetString(next.ToBytes());
            _ = Core.Security.SecretPresentation.MaskDocument("config.toml", text);
            Apply(text);
            Message = null;
        }
        catch (Exception ex) when (ex is TomlEditException or ArgumentException)
        { Message = "변경을 적용하지 못했습니다. 이름 충돌과 TOML 형식을 확인하세요."; }
    }
}
