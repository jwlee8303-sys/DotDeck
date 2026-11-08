using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotDeck.App.ViewModels.Tools;
using DotDeck.Core.Editors;
using DotDeck.Core.Hooks;

namespace DotDeck.App.ViewModels.Editor;

/// <summary>A product-neutral hook table; catalogs, suggestions, persistence and testing are supplied by its owner.</summary>
public sealed partial class HookTableViewModel : ObservableObject
{
    private bool _loading;
    private IHookStore? _store;

    public HookTableViewModel() => Rows.CollectionChanged += (_, _) => NotifyChanged();

    public ObservableCollection<HookRow> Rows { get; } = [];

    public Func<HookEventCatalog?>? GetEvents { get; init; }

    public HookEventCatalog? Events => GetEvents?.Invoke();

    public Func<HookEventInfo?, IReadOnlyList<ChoiceItem>>? GetMatcherItems { get; init; }

    public Func<IReadOnlyList<string>>? MatcherHelp { get; init; }

    public Action? Changed { get; init; }

    public Action<HookRow>? RunHook { get; init; }

    public Action<RoleDescription?>? ShowHelp { get; init; }

    [ObservableProperty]
    private HookRow? _selectedRow;

    public IReadOnlyList<ChoiceItem> HookEventChoices => Events is null ? []
        : [.. Events.Ordered.Select(e => new ChoiceItem(e.Name, e.Name,
            string.Join(Environment.NewLine, e.HelpLines()), e.Common ? "자주 쓰는 이벤트" : "기타 이벤트"))];

    public IReadOnlyList<ChoiceItem> MatcherItemsFor(HookEventInfo? info) => GetMatcherItems?.Invoke(info) ?? [];

    public void Load(IHookStore store)
    {
        _loading = true;
        try
        {
            _store = store;
            foreach (var old in Rows)
            {
                old.Changed = null;
                old.Owner = null;
            }

            Rows.Clear();
            SelectedRow = null;
            foreach (var h in store.Read())
            {
                Rows.Add(Wire(new HookRow
                {
                    Event = h.Event,
                    Matcher = h.Matcher,
                    Command = h.Command,
                    Timeout = h.Timeout?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    Source = h,
                }));
            }
        }
        finally
        {
            _loading = false;
        }
    }

    public void Write() => _store?.Write([.. Rows.Where(h => h.Event.Trim().Length > 0).Select(h => new HookHandlerModel(
        h.Source is not null && h.Event == h.Source.Event ? h.Event : h.Event.Trim(),
        h.Source is not null && h.Matcher == h.Source.Matcher ? h.Matcher : h.Matcher.Trim(),
        h.Command,
        int.TryParse(h.Timeout, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeout) ? timeout : null,
        h.Source?.CommandExtra, h.Source?.GroupExtra, h.Source?.GroupKey))]);

    [RelayCommand]
    private void AddHook(string? evt) => Rows.Add(Wire(new HookRow { Event = evt ?? Events?.Ordered.FirstOrDefault()?.Name ?? string.Empty }));

    [RelayCommand]
    private void Remove(HookRow? row)
    {
        if (row is not null && Rows.Remove(row))
        {
            row.Changed = null;
            row.Owner = null;
            if (SelectedRow == row)
            {
                SelectedRow = null;
            }
        }
    }

    [RelayCommand]
    private void MoveUp(HookRow? row) => Move(row, -1);

    [RelayCommand]
    private void MoveDown(HookRow? row) => Move(row, 1);

    public void Move(HookRow? row, int delta)
    {
        var index = row is null ? -1 : Rows.IndexOf(row);
        if (index >= 0 && index + delta >= 0 && index + delta < Rows.Count)
        {
            Rows.Move(index, index + delta);
        }
    }

    [RelayCommand]
    private void Run(HookRow? row)
    {
        if (row?.IsCommandHook == true)
        {
            RunHook?.Invoke(row);
        }
    }

    partial void OnSelectedRowChanged(HookRow? value) => UpdateHelp();

    private HookRow Wire(HookRow row)
    {
        row.Owner = this;
        row.Changed = (changed, _) =>
        {
            NotifyChanged();
            if (changed == SelectedRow)
            {
                UpdateHelp();
            }
        };
        return row;
    }

    private void NotifyChanged()
    {
        if (!_loading)
        {
            Changed?.Invoke();
        }
    }

    private void UpdateHelp()
    {
        if (_loading)
        {
            return;
        }

        if (SelectedRow is not { } row)
        {
            ShowHelp?.Invoke(null);
            return;
        }

        var lines = row.EventInfo?.HelpLines().ToList() ?? ["목록에 없는 이벤트입니다. 값은 그대로 보존됩니다."];
        if (row.EventInfo?.MatchesTools == true)
        {
            lines.AddRange(MatcherHelp?.Invoke() ?? []);
        }

        lines.Add("matcher 작성법: 비우거나 *는 전체, Read|Write처럼 | 로 여러 개, 그 밖의 글자가 있으면 정규식(앞뒤 고정 없음).");
        if (!row.IsCommandHook)
        {
            lines.Add($"이 훅은 {row.Type} 형식입니다. 내용은 텍스트 보기에서 고치세요.");
        }

        ShowHelp?.Invoke(new RoleDescription($"훅 · {row.Event}", lines));
    }
}

/// <summary>One hook command: event, matcher, command and timeout (other handler types are kept as they are).</summary>
public sealed partial class HookRow : FormRow
{
    internal HookTableViewModel? Owner { get; set; }

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

    internal HookHandlerModel? Source { get; init; }

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
