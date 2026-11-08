using System.IO;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotDeck.App.ViewModels.Tools;
using DotDeck.Core.Editors;
using DotDeck.Core.Mcp;
using DotDeck.Core.Roles;

namespace DotDeck.App.ViewModels.Editor;

/// <summary>
/// A permission rule as <c>[kind] [tool] [argument]</c>. A rule the form did not edit is written back as its original
/// text; one that does not parse (<see cref="IsParsed"/> false) is shown and written as a single text.
/// </summary>
public sealed partial class PermissionRow : FormRow
{
    private bool _loading;

    public PermissionRow()
    {
    }

    /// <summary>Row for an existing rule: split into tool and argument, original text kept.</summary>
    public PermissionRow(string kind, string raw)
    {
        _loading = true;
        var rule = PermissionRule.Parse(raw);
        Kind = kind;
        Raw = raw;
        IsParsed = rule.IsParsed;
        Tool = rule.Tool;
        Argument = rule.Argument ?? string.Empty;
        _loading = false;
    }

    internal SettingsFormViewModel? Owner { get; set; }

    [ObservableProperty]
    private string _kind = "allow";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArgumentPlaceholder), nameof(ToolDescription))]
    private string _tool = string.Empty;

    [ObservableProperty]
    private string _argument = string.Empty;

    /// <summary>Warnings for this rule (also listed in the validation pane); saving is never blocked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarnings), nameof(HasNotesOnly), nameof(IssueText))]
    private IReadOnlyList<ValidationIssue> _issues = [];

    public bool HasWarnings => Issues.Any(i => i.Severity != IssueSeverity.Info);

    public bool HasNotesOnly => Issues.Count > 0 && !HasWarnings;

    public string IssueText => string.Join(Environment.NewLine, Issues.Select(i => i.Message));

    private ToolArgumentKind? ArgumentKind => Owner?.Tools?.ArgumentOf(Tool.Trim());

    /// <summary>How the argument cell works for the chosen tool (free text for unknown tools).</summary>
    public ArgumentInput Input => ArgumentKind?.Input ?? (IsMcp ? ArgumentInput.Mcp : ArgumentInput.Text);

    /// <summary><c>Tool(name:value)</c> deny/ask rule (shown and kept as typed).</summary>
    public PermissionParameter? Parameter =>
        Owner?.Tools is { } tools && IsParsed ? PermissionRule.ParameterOf(Tool, Argument, tools) : null;

    public bool IsMcp => IsParsed && PermissionRule.IsMcp(Tool.Trim());

    public McpRuleParts? Mcp => IsMcp ? McpRuleParts.Parse(Tool, Owner?.McpServerNames) : null;

    /// <summary>The argument box: hidden for MCP rules unless one already has (wrong) parentheses to clear.</summary>
    public bool ShowArgumentBox => IsParsed && (!IsMcp || Argument.Length > 0);

    public bool ShowMcpScope => Mcp is not null;

    public bool IsArgumentEnabled => Input is not (ArgumentInput.Disabled or ArgumentInput.Mcp) || Argument.Trim().Length > 0;

    public bool HasArgumentChoices => Input == ArgumentInput.Choice;

    public string? Prefix => ArgumentKind is { Input: ArgumentInput.Prefixed, Prefix: { } prefix } ? prefix : null;

    /// <summary>The prefix is shown outside the box while the argument has it (or is empty); other text is shown as typed.</summary>
    public bool UsesPrefix =>
        Prefix is { } prefix && Parameter is null && (Argument.Length == 0 || Argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>The text in the argument box: <see cref="Argument"/> without the shown prefix.</summary>
    public string ArgumentValue
    {
        get => UsesPrefix && Argument.Length >= Prefix!.Length ? Argument[Prefix.Length..] : Argument;
        set
        {
            var text = value ?? string.Empty;
            if (UsesPrefix)
            {
                var prefix = Prefix!;
                Argument = text.Length == 0 ? string.Empty
                    : text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? text
                    : prefix + text;
            }
            else
            {
                Argument = text;
            }
        }
    }

    public string McpServer => Mcp?.Server ?? string.Empty;

    public bool IsMcpToolScope => Mcp?.Scope == McpRuleScope.Tool;

    private static readonly IReadOnlyList<ChoiceItem> s_mcpScopes =
    [
        new("server", "서버 전체 (mcp__서버)", "그 서버의 모든 도구. mcp__서버__*와 같습니다."),
        new("all", "모든 도구 (mcp__서버__*)", "그 서버의 모든 도구를 와일드카드로 씁니다."),
        new("tool", "특정 도구 (mcp__서버__도구)", "도구 하나만. 오른쪽 칸에 도구 이름을 씁니다."),
    ];

    public IReadOnlyList<ChoiceItem> McpScopeChoices => s_mcpScopes;

    public string McpScopeLabel => Mcp?.Scope switch
    {
        McpRuleScope.AllTools => "모든 도구 (__*)",
        McpRuleScope.Tool => "특정 도구",
        _ => "서버 전체",
    };

    /// <summary>Tool name of a <c>mcp__server__tool</c> rule; typing one switches to that scope.</summary>
    public string McpToolName
    {
        get => Mcp?.Scope == McpRuleScope.Tool ? Mcp.ToolName : string.Empty;
        set
        {
            if (Mcp is { } parts)
            {
                var name = (value ?? string.Empty).Trim();
                Tool = (name.Length == 0 && parts.Scope != McpRuleScope.Tool ? parts : parts with { Scope = McpRuleScope.Tool, ToolName = name }).ToolText;
            }
        }
    }

    private string _mcpToolDraft = string.Empty;

    [RelayCommand]
    private void SetMcpScope(string? value)
    {
        var scope = value switch
        {
            "all" => McpRuleScope.AllTools,
            "tool" => McpRuleScope.Tool,
            _ => McpRuleScope.Server,
        };
        if (Mcp is not { } parts || parts.Scope == scope)
        {
            return;
        }

        if (parts.Scope == McpRuleScope.Tool)
        {
            _mcpToolDraft = parts.ToolName;
        }

        Tool = (parts with { Scope = scope, ToolName = scope == McpRuleScope.Tool ? _mcpToolDraft : string.Empty }).ToolText;
    }

    private static readonly string[] s_displayProperties =
    [
        nameof(Input), nameof(Parameter), nameof(IsMcp), nameof(Mcp), nameof(ShowArgumentBox), nameof(ShowMcpScope),
        nameof(IsArgumentEnabled), nameof(HasArgumentChoices), nameof(Prefix), nameof(UsesPrefix), nameof(ArgumentValue),
        nameof(McpServer), nameof(IsMcpToolScope), nameof(McpScopeLabel), nameof(McpToolName),
        nameof(ArgumentPlaceholder), nameof(ToolDescription),
    ];

    /// <summary>Re-reads every derived display property (tool, argument, catalog or MCP servers changed).</summary>
    internal void RefreshDisplay()
    {
        foreach (var name in s_displayProperties)
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>False for text that is not <c>Tool</c> or <c>Tool(argument)</c>: then <see cref="Tool"/> holds all of it.</summary>
    [ObservableProperty]
    private bool _isParsed = true;

    /// <summary>The rule as it was read (null for new rows).</summary>
    public string? Raw { get; private set; }

    private bool _edited;

    /// <summary>An existing rule whose tool and argument were not edited: saved exactly as read (spaces included).</summary>
    public bool IsUnchanged => !_edited && Raw is not null;

    /// <summary>The text to save: the original until edited, then <c>Tool(argument)</c> (trimmed).</summary>
    public string RuleText =>
        IsUnchanged ? Raw!
        : IsParsed ? PermissionRule.Compose(Tool, Argument)
        : Tool.Trim();

    public string ArgumentPlaceholder => Owner?.PlaceholderFor(Tool) ?? string.Empty;

    public string? ToolDescription => Owner?.DescriptionFor(Tool);

    [RelayCommand]
    private void SetKind(string? value) => Kind = value ?? Kind;

    [RelayCommand]
    private void SetTool(string? value) => Tool = value ?? Tool;

    [RelayCommand]
    private void SetArgument(string? value) => Argument = value ?? Argument;

    partial void OnToolChanged(string value)
    {
        MarkEdited();
        if (!_loading)
        {
            RefreshDisplay();
        }
    }

    partial void OnArgumentChanged(string value)
    {
        MarkEdited();
        if (!_loading)
        {
            RefreshDisplay();
        }
    }

    private void MarkEdited()
    {
        if (!_loading)
        {
            _edited = true;
        }
    }

    protected override bool IsDisplayOnly(string? propertyName) =>
        _loading || propertyName is nameof(IsParsed) or nameof(Issues) or nameof(HasWarnings) or nameof(HasNotesOnly) or nameof(IssueText)
        || s_displayProperties.Contains(propertyName);
}

/// <summary>One hook command: event, matcher, command and timeout (other handler types are kept as they are).</summary>
public sealed partial class HookRow : FormRow
{
    internal SettingsFormViewModel? Owner { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesMatcher), nameof(MatcherPlaceholder), nameof(EventDescription), nameof(MatcherWarning))]
    private string _event = "PreToolUse";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatcherWarning))]
    private string _matcher = string.Empty;

    [ObservableProperty]
    private string _command = string.Empty;

    [ObservableProperty]
    private string _timeout = string.Empty;

    internal HookRule? Source { get; init; }

    /// <summary>Handler type; only <c>command</c> hooks have an editable command line.</summary>
    public string Type => Source?.Type ?? "command";

    public bool IsCommandHook => Type == "command";

    public string CommandToolTip => IsCommandHook ? "실행할 명령" : $"{Type} 형식 훅입니다. 내용은 JSON 보기에서 고치세요(그대로 보존됩니다).";

    public HookEventInfo? EventInfo => Owner?.Events?.Find(Event.Trim());

    /// <summary>False for events that ignore matchers (an existing value is kept and still shown).</summary>
    public bool UsesMatcher => EventInfo?.UsesMatcher ?? true;

    public string MatcherPlaceholder => EventInfo switch
    {
        null => "matcher (비우면 전체)",
        { UsesMatcher: false } => "이 이벤트는 matcher를 사용하지 않음",
        { MatchesTools: true } => "도구 이름 (비우면 전체)",
        var info => $"{info.MatcherTarget ?? "값"} (비우면 전체)",
    };

    public string? EventDescription => EventInfo is { } info ? string.Join(Environment.NewLine, info.HelpLines()) : "목록에 없는 이벤트입니다. 값은 그대로 보존됩니다.";

    public string? MatcherWarning => HookMatcher.Parse(Matcher, HookMatcher.SyntaxOf(EventInfo)).RegexError();

    [RelayCommand]
    private void SetEvent(string? value) => Event = value ?? Event;

    /// <summary>The matcher menu: "all", then check boxes for tools (and MCP servers) or the event's documented values.</summary>
    public IReadOnlyList<MatcherChoice> MatcherChoices()
    {
        var parsed = HookMatcher.Parse(Matcher, HookMatcher.SyntaxOf(EventInfo));
        var choices = new List<MatcherChoice> { new("*", "전체 (* 또는 비움)", "모든 값에 실행합니다.", parsed.Mode == HookMatcherMode.All, null) };
        var items = Owner?.MatcherItemsFor(EventInfo) ?? [];
        choices.AddRange(items.Select(i => new MatcherChoice(i.Value, i.Label, i.Description, parsed.Contains(i.Value), i.Group)));

        // Names in the matcher that the lists do not have stay visible (and can be unchecked).
        foreach (var extra in parsed.Items.Where(v => items.All(i => i.Value != v)))
        {
            choices.Add(new MatcherChoice(extra, extra, "직접 입력한 값", true, "직접 입력"));
        }

        return choices;
    }

    public void ToggleMatcher(string value, bool on)
    {
        if (value == "*")
        {
            Matcher = on ? string.Empty : Matcher;
            return;
        }

        Matcher = HookMatcher.Parse(Matcher, HookMatcher.SyntaxOf(EventInfo)).Toggle(value, on);
    }

    protected override bool IsDisplayOnly(string? propertyName) =>
        propertyName is nameof(UsesMatcher) or nameof(MatcherPlaceholder) or nameof(EventDescription) or nameof(MatcherWarning);
}

public sealed partial class EnvRow : FormRow
{
    [ObservableProperty]
    private string _key = string.Empty;

    [ObservableProperty]
    private string _value = string.Empty;
}

/// <summary>Claude Code settings.json: permissions, hooks and env tables. Other keys are kept untouched.</summary>
public sealed partial class SettingsFormViewModel : DocumentFormViewModel
{
    private ClaudeSettingsDocument? _doc;
    private int _mcpVersion;

    public SettingsFormViewModel(Action<string> apply)
        : base(apply)
    {
        foreach (var collection in new INotifyCollectionChanged[] { Permissions, Hooks, Env })
        {
            collection.CollectionChanged += (_, _) => Commit();
        }
    }

    public IReadOnlyList<string> PermissionKinds => ClaudeSettingsDocument.PermissionKinds;

    public ObservableCollection<PermissionRow> Permissions { get; } = [];

    public ObservableCollection<HookRow> Hooks { get; } = [];

    public ObservableCollection<EnvRow> Env { get; } = [];

    /// <summary>Raised by <see cref="ShellViewModel"/> to run a hook row in the hook test pane.</summary>
    public Action<HookRow>? RunHook { get; set; }

    /// <summary>Where the settings file sits (global / project / local), or null when it was opened as a form manually.</summary>
    public FileRole? Role { get; init; }

    public string FilePath { get; init; } = string.Empty;

    public SettingsKeyCatalog? SettingsKeys { get; init; }

    public ClaudeToolCatalog? Tools { get; init; }

    public HookEventCatalog? Events { get; init; }

    /// <summary>Finds MCP servers for a project root (null: user settings). Read-only parsing, off the UI thread.</summary>
    public Func<string?, Task<McpDiscoveryResult>>? DiscoverMcp { get; init; }

    /// <summary>Finds subagent names (agents/*.md) for a project root (null: user agents only), off the UI thread.</summary>
    public Func<string?, Task<IReadOnlyList<AgentNameInfo>>>? DiscoverAgents { get; init; }

    /// <summary>Shows help for the selected row in the properties pane.</summary>
    public Action<RoleDescription?>? ShowHelp { get; init; }

    /// <summary>e.g. "settings.json · 프로젝트 · 기준 D:\project\DotDeck".</summary>
    public string RoleSummary => Role is null
        ? "Claude Code 설정 위치가 아닌 파일을 설정 폼으로 열었습니다. 범위별 안내는 표시하지 않습니다."
        : $"{Role.Summary(FilePath)} · 기준 {Role.BaseRoot}";

    public IReadOnlyList<string> RoleNotes => Role?.Notes ?? [];

    /// <summary>Keys this file sets that do not apply at its level (refreshed on every load and edit).</summary>
    [ObservableProperty]
    private IReadOnlyList<ValidationIssue> _scopeIssues = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolChoices), nameof(McpServerNames))]
    private IReadOnlyList<McpServerInfo> _mcpServers = [];

    [ObservableProperty]
    private string _mcpStatus = "MCP 서버 찾는 중…";

    [ObservableProperty]
    private string _mcpDetails = string.Empty;

    /// <summary>Configuration files that exist but could not be read (path and reason only).</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _mcpProblems = [];

    /// <summary>The server list under "MCP 서버 N개" is open.</summary>
    [ObservableProperty]
    private bool _isMcpListOpen;

    /// <summary>Names offered for <c>Agent(이름)</c>: documented built-ins, then agents/*.md of this scope.</summary>
    [ObservableProperty]
    private IReadOnlyList<ChoiceItem> _agentChoices = [];

    public IReadOnlyList<string> McpServerNames => [.. McpServers.Select(s => s.Name)];

    [ObservableProperty]
    private FormRow? _selectedRow;

    public IReadOnlyList<ChoiceItem> HookEventChoices =>
        Events is null
            ? [.. ClaudeSettingsDocument.HookEvents.Select(e => new ChoiceItem(e, e))]
            : [.. Events.Ordered.Select(e => new ChoiceItem(e.Name, e.Name, string.Join(Environment.NewLine, e.HelpLines()), e.Common ? "자주 쓰는 이벤트" : "기타 이벤트"))];

    public const string McpGroup = "MCP 도구";

    /// <summary>Tool menu of a permission row: built-in tools, then MCP servers (all tools of a server).</summary>
    public IReadOnlyList<ChoiceItem> ToolChoices =>
    [
        .. ToolItems(),
        .. McpServers.Select(s => new ChoiceItem(s.PermissionRule, s.PermissionRule,
            $"{s.Name} 서버의 모든 도구. 고른 뒤 모든 도구(__*)나 특정 도구로 바꿀 수 있습니다.{Environment.NewLine}{SourceText(s)}", McpGroup)),
    ];

    private static string SourceText(McpServerInfo server) => $"출처: {server.Source}{Environment.NewLine}{server.SourcePath}";

    private IEnumerable<ChoiceItem> ToolItems() =>
        Tools is null
            ? FrontmatterFormViewModel.KnownTools.Select(t => new ChoiceItem(t, t))
            : Tools.Ordered.Select(t => new ChoiceItem(t.Name, t.Name, t.Description, t.Common ? "기본 도구" : "기타 도구"));

    /// <summary>Check box items of a matcher for <paramref name="info"/> (unknown events get tools).</summary>
    public IReadOnlyList<ChoiceItem> MatcherItemsFor(HookEventInfo? info)
    {
        if (info is { UsesMatcher: true, MatchesTools: false })
        {
            return [.. (info.Values ?? []).Select(v => new ChoiceItem(v, v, null, info.MatcherTarget))];
        }

        return
        [
            .. ToolItems(),
            .. McpServers.Select(s => new ChoiceItem(s.MatcherPattern, s.MatcherPattern, $"{s.Name} 서버의 모든 도구{Environment.NewLine}{SourceText(s)}", McpGroup)),
        ];
    }

    public string PlaceholderFor(string tool) =>
        Tools?.ArgumentOf(tool.Trim())?.Placeholder
        ?? (PermissionRule.IsMcp(tool) ? "MCP 규칙은 괄호 없이 씁니다 (도구 하나: mcp__서버__도구)" : "인수 (비우면 도구 전체)");

    public string? DescriptionFor(string tool) =>
        Tools is null ? null : string.Join(Environment.NewLine, Tools.PermissionHelp(tool.Trim(), Role));

    protected override string? LoadCore(string text)
    {
        var doc = ClaudeSettingsDocument.TryParse(text, out var error);
        if (doc is null)
        {
            return error + " JSON을 고친 뒤 폼으로 전환할 수 있습니다.";
        }

        _doc = doc;
        UpdateScopeIssues();
        Reset(Permissions, ClaudeSettingsDocument.PermissionKinds
            .SelectMany(kind => doc.GetPermissions(kind).Select(rule => new PermissionRow(kind, rule))));
        Reset(Hooks, doc.GetHooks().Select(h => new HookRow
        {
            Event = h.Event,
            Matcher = h.Matcher,
            Command = h.Command,
            Timeout = h.Timeout?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            Source = h,
        }));
        Reset(Env, doc.GetEnv().Select(p => new EnvRow { Key = p.Key, Value = p.Value }));
        UpdatePermissionIssues();
        if (_mcpVersion == 0)
        {
            AgentChoices = BuildAgentChoices([]);
            _ = RefreshMcpAsync();
        }

        return null;
    }

    private IReadOnlyList<ChoiceItem> BuildAgentChoices(IReadOnlyList<AgentNameInfo> found)
    {
        var builtIn = Tools?.ArgumentKinds.TryGetValue("agent", out var kind) == true ? kind.Choices : [];
        return
        [
            .. builtIn.Select(n => new ChoiceItem(n, n, "Claude Code 기본 서브에이전트", "기본 에이전트")),
            .. found.Where(a => !builtIn.Contains(a.Name, StringComparer.Ordinal))
                .Select(a => new ChoiceItem(a.Name, a.Name, $"출처: {a.Source}{Environment.NewLine}{a.Path}", "이 범위의 에이전트")),
        ];
    }

    [RelayCommand]
    private async Task RefreshMcpAsync()
    {
        if (DiscoverMcp is null)
        {
            McpStatus = "MCP 서버 검색을 사용할 수 없습니다.";
            return;
        }

        var version = ++_mcpVersion;
        McpStatus = "MCP 서버 찾는 중…";
        McpDiscoveryResult result;
        try
        {
            result = await DiscoverMcp(Role?.ProjectRoot).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result = new McpDiscoveryResult([], ["MCP 설정을 읽을 수 없습니다."]);
        }

        if (version != _mcpVersion)
        {
            return;
        }

        McpServers = result.Servers;
        McpProblems = result.Problems;
        McpStatus = $"MCP 서버 {result.Servers.Count}개" + (result.Problems.Count > 0 ? $" · 읽지 못한 파일 {result.Problems.Count}개" : string.Empty);
        var lines = result.Servers.Select(s => $"{s.Name} — {s.Source}").Concat(result.Problems).ToList();
        McpDetails = lines.Count == 0 ? "설정 파일에서 MCP 서버를 찾지 못했습니다(서버를 실행하거나 연결하지 않습니다)." : string.Join(Environment.NewLine, lines);

        // Server names decide where an mcp__ tool name splits into server and tool.
        foreach (var row in Permissions)
        {
            row.RefreshDisplay();
        }

        if (DiscoverAgents is not null)
        {
            IReadOnlyList<AgentNameInfo> agents;
            try
            {
                agents = await DiscoverAgents(Role?.ProjectRoot).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                agents = [];
            }

            if (version == _mcpVersion)
            {
                AgentChoices = BuildAgentChoices(agents);
            }
        }
    }

    [RelayCommand]
    private void ToggleMcpList() => IsMcpListOpen = !IsMcpListOpen;

    [RelayCommand]
    private void AddPermission(string? kind) => Permissions.Add(Wire(new PermissionRow { Kind = kind ?? "allow" }));

    [RelayCommand]
    private void AddHook(string? evt) => Hooks.Add(Wire(new HookRow { Event = evt ?? "PreToolUse" }));

    [RelayCommand]
    private void AddEnv() => Env.Add(Wire(new EnvRow()));

    [RelayCommand]
    private void Remove(FormRow? row)
    {
        switch (row)
        {
            case PermissionRow p: Permissions.Remove(p); break;
            case HookRow h: Hooks.Remove(h); break;
            case EnvRow e: Env.Remove(e); break;
        }
    }

    [RelayCommand]
    private void MoveUp(FormRow? row) => Move(row, -1);

    [RelayCommand]
    private void MoveDown(FormRow? row) => Move(row, +1);

    [RelayCommand]
    private void Run(HookRow? row)
    {
        if (row is not null)
        {
            RunHook?.Invoke(row);
        }
    }

    partial void OnSelectedRowChanged(FormRow? value) => UpdateHelp();

    /// <summary>Help for the selected row: the tool's rule syntax, or the hook event.</summary>
    private void UpdateHelp()
    {
        RoleDescription? help = SelectedRow switch
        {
            PermissionRow p when p.IsParsed && p.Tool.Trim().Length > 0 =>
                new RoleDescription($"권한 규칙 · {p.Kind} · {p.Tool.Trim()}", PermissionHelp(p)),
            PermissionRow p => new RoleDescription("권한 규칙", [p.IsParsed ? "도구를 고르거나 직접 입력하세요." : "형식을 해석할 수 없는 규칙입니다. 고치지 않으면 원문 그대로 저장합니다."]),
            HookRow h => new RoleDescription($"훅 · {h.Event}", HookHelp(h)),
            _ => null,
        };
        ShowHelp?.Invoke(help);
    }

    private List<string> PermissionHelp(PermissionRow row)
    {
        var lines = row.Issues.Select(i => (i.Severity == IssueSeverity.Info ? "참고: " : "⚠ ") + i.Message).ToList();
        if (row.Parameter is not null && Tools is not null)
        {
            lines.Add($"매개변수 규칙: {row.Parameter.Name} = {row.Parameter.Value}");
            lines.AddRange(Tools.ParameterHelp());
        }

        lines.AddRange(Tools?.PermissionHelp(row.Tool.Trim(), Role) ?? []);
        return lines;
    }

    private List<string> HookHelp(HookRow row)
    {
        var lines = row.EventInfo is { } info ? info.HelpLines().ToList() : ["목록에 없는 이벤트입니다. 값은 그대로 보존됩니다."];
        if (row.EventInfo is { MatchesTools: true } && Tools is not null)
        {
            lines.AddRange(Tools.Mcp.MatcherHelp);
        }

        lines.Add("matcher 작성법: 비우거나 *는 전체, Read|Write처럼 | 로 여러 개, 그 밖의 글자가 있으면 정규식(앞뒤 고정 없음).");
        if (!row.IsCommandHook)
        {
            lines.Add($"이 훅은 {row.Type} 형식입니다. 내용은 JSON 보기에서 고치세요.");
        }

        return lines;
    }

    private void Move(FormRow? row, int delta)
    {
        switch (row)
        {
            case PermissionRow p: Shift(Permissions, p, delta); break;
            case HookRow h: Shift(Hooks, h, delta); break;
            case EnvRow e: Shift(Env, e, delta); break;
        }
    }

    private static void Shift<T>(ObservableCollection<T> list, T item, int delta)
    {
        var from = list.IndexOf(item);
        var to = from + delta;
        if (from >= 0 && to >= 0 && to < list.Count)
        {
            list.Move(from, to);
        }
    }

    private void Reset<T>(ObservableCollection<T> target, IEnumerable<T> rows)
        where T : FormRow
    {
        target.Clear();
        foreach (var row in rows)
        {
            target.Add(Wire(row));
        }
    }

    private T Wire<T>(T row)
        where T : FormRow
    {
        switch (row)
        {
            case PermissionRow p: p.Owner = this; break;
            case HookRow h: h.Owner = this; break;
        }

        row.Changed = OnRowChanged;
        return row;
    }

    private void OnRowChanged(FormRow row, string? property)
    {
        Commit();
        if (row == SelectedRow && property is nameof(PermissionRow.Tool) or nameof(PermissionRow.Kind) or nameof(PermissionRow.Argument) or nameof(HookRow.Event))
        {
            UpdateHelp();
        }
    }

    /// <summary>Row warnings from the same checks as the validation pane (rules that would be saved).</summary>
    private void UpdatePermissionIssues()
    {
        var rows = Permissions.Where(p => p.IsUnchanged || p.RuleText.Length > 0).ToList();
        var issues = PermissionLint.Check([.. rows.Select(r => new PermissionEntry(r.Kind, r.RuleText))], Tools);
        var byRow = new Dictionary<PermissionRow, List<ValidationIssue>>();
        foreach (var issue in issues)
        {
            foreach (var index in new[] { issue.Index, issue.OtherIndex }.OfType<int>())
            {
                if (!byRow.TryGetValue(rows[index], out var list))
                {
                    byRow[rows[index]] = list = [];
                }

                list.Add(issue.Issue);
            }
        }

        foreach (var row in Permissions)
        {
            row.Issues = byRow.TryGetValue(row, out var list) ? list : [];
        }
    }

    private void Commit()
    {
        if (IsLoading || _doc is null)
        {
            return;
        }

        foreach (var kind in ClaudeSettingsDocument.PermissionKinds)
        {
            // Empty new or edited rows are left out; rules read from the file are kept as they are.
            _doc.SetPermissions(kind, Permissions.Where(p => p.Kind == kind && (p.IsUnchanged || p.RuleText.Length > 0)).Select(p => p.RuleText).ToList());
        }

        _doc.SetHooks(Hooks.Where(h => h.Event.Trim().Length > 0)
            .Select(h => new HookRule(
                h.Source is not null && h.Event == h.Source.Event ? h.Event : h.Event.Trim(),
                // A regex matcher's spaces are significant: keep the matcher as read unless it was changed.
                h.Source is not null && h.Matcher == h.Source.Matcher ? h.Matcher : h.Matcher.Trim(),
                h.Command,
                int.TryParse(h.Timeout, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) ? t : null,
                h.Source?.CommandExtra,
                h.Source?.GroupExtra,
                h.Source?.GroupKey))
            .ToList());
        _doc.SetEnv(Env.Select(e => new KeyValuePair<string, string>(e.Key, e.Value)).ToList());
        UpdateScopeIssues();
        UpdatePermissionIssues();
        Apply(_doc.ToJson());
    }

    private void UpdateScopeIssues() =>
        ScopeIssues = _doc is not null && Role is not null && SettingsKeys is not null ? _doc.CheckScope(Role.Scope, SettingsKeys) : [];
}
