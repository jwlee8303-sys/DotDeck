using DotDeck.Core.Editors;
using DotDeck.Core.Hooks;

namespace DotDeck.App.ViewModels.Tools;

internal static class ClaudeHookProfile
{
    public static HookTestProfile Default { get; } = new("claude", ClaudeSettingsDocument.HookEvents,
        HookSamples.For, directory => new Dictionary<string, string> { ["CLAUDE_PROJECT_DIR"] = directory });
}
