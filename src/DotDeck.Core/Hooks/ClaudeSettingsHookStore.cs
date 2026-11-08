using DotDeck.Core.Editors;

namespace DotDeck.Core.Hooks;

/// <summary>Preserves Claude's matcher groups and unknown handler fields through its settings document.</summary>
public sealed class ClaudeSettingsHookStore(ClaudeSettingsDocument document) : IHookStore
{
    public IReadOnlyList<HookHandlerModel> Read() => document.GetHooks();

    public void Write(IReadOnlyList<HookHandlerModel> hooks) => document.SetHooks(hooks);
}
