using System.Windows.Input;
using DotDeck.App.ViewModels.Documents;
using DotDeck.Core.Editors;
using DotDeck.Core.Editors.Registry;
using DotDeck.Core.Roles;

namespace DotDeck.App.ViewModels.Editor;

/// <summary>Product-specific dependencies, separate from document tab operations.</summary>
public interface IClaudeFormServices
{
    SettingsKeyCatalog? SettingsKeys { get; }

    ClaudeToolCatalog? ToolCatalog { get; }

    HookEventCatalog? HookEvents { get; }

    void RunHook(HookRow row);

    Task<Core.Mcp.McpDiscoveryResult> DiscoverMcpAsync(string? projectRoot);

    Task<IReadOnlyList<AgentNameInfo>> DiscoverAgentsAsync(string? projectRoot);
}

public interface ICodexFormServices
{
    Core.Codex.ConfigFormCatalog CodexCatalog { get; }
    string? PickProjectFolder();
}

/// <summary>One format choice, bound identically by document and explorer menus.</summary>
public sealed record EditorChoice(string Label, string Name, ICommand Command);

public sealed record FormContext(Action<string> Apply, string Path, FileRole? Role, ITextDocumentHost Host);

/// <summary>UI form factories. Register a factory here when its Core descriptor gains form support.</summary>
public static class FormRegistry
{
    private static readonly IReadOnlyDictionary<EditorKind, Func<FormContext, DocumentFormViewModel>> s_factories =
        new Dictionary<EditorKind, Func<FormContext, DocumentFormViewModel>>
        {
            [EditorKind.ClaudeSettings] = CreateSettings,
            [EditorKind.Agent] = c => CreateFrontmatter(EditorKind.Agent, c),
            [EditorKind.Skill] = c => CreateFrontmatter(EditorKind.Skill, c),
            [EditorKind.CodexConfig] = c => new CodexConfigFormViewModel(c.Apply, (c.Host as ICodexFormServices)?.CodexCatalog)
            { PickFolder = () => (c.Host as ICodexFormServices)?.PickProjectFolder() },
        };

    public static bool HasForm(EditorKind kind) => EditorRegistry.Get(kind).HasForm && s_factories.ContainsKey(kind);

    public static DocumentFormViewModel? Create(EditorKind kind, FormContext context) =>
        HasForm(kind) ? s_factories[kind](context) : null;

    public static IReadOnlyList<EditorChoice> Choices(ICommand command) =>
    [
        .. EditorRegistry.All
            .Where(d => d.Kind is EditorKind.Text or EditorKind.Markdown || HasForm(d.Kind))
            .Select(d => new EditorChoice(d.Label, d.Name, command)),
        new("자동 판별로 되돌리기", "auto", command),
    ];

    private static FrontmatterFormViewModel CreateFrontmatter(EditorKind kind, FormContext context) =>
        new(kind, context.Apply, (context.Host as IClaudeFormServices)?.ToolCatalog?.Ordered.Where(t => t.Common).Select(t => t.Name).ToList());

    private static SettingsFormViewModel CreateSettings(FormContext context)
    {
        var services = context.Host as IClaudeFormServices;
        return new SettingsFormViewModel(context.Apply)
        {
            RunHook = services is null ? null : services.RunHook,
            Role = context.Role,
            FilePath = context.Path,
            SettingsKeys = services?.SettingsKeys,
            Tools = services?.ToolCatalog,
            Events = services?.HookEvents,
            DiscoverMcp = services is null ? null : services.DiscoverMcpAsync,
            DiscoverAgents = services is null ? null : services.DiscoverAgentsAsync,
            ShowHelp = context.Host.ShowFormHelp,
        };
    }
}
