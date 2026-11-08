using System.Text.Json.Nodes;
using DotDeck.App.ViewModels.Editor;
using DotDeck.App.ViewModels.Tools;
using DotDeck.Core.Editors;
using DotDeck.Core.Hooks;

namespace DotDeck.App.Tests;

public sealed class HookTableTests
{
    [Fact]
    public void Standalone_table_uses_its_adapter_and_keeps_unknown_handler_data()
    {
        var doc = ClaudeSettingsDocument.TryParse("""
            {"hooks":{"PreToolUse":[{"matcher":"  ^Read$  ","extra":"group","hooks":[
              {"type":"command","command":"echo a","extra":"handler"},
              {"type":"prompt","prompt":"keep me"}]}]},"unknown":42}
            """, out _)!;
        var changes = 0;
        var runs = 0;
        var table = new HookTableViewModel { GetEvents = () => HookEventCatalog.BuiltIn, Changed = () => changes++, RunHook = _ => runs++ };
        table.Load(new ClaudeSettingsHookStore(doc));
        Assert.Equal(0, changes);
        Assert.Equal(2, table.Rows.Count);
        table.Rows[0].Command = "echo b";
        Assert.Equal(1, changes);
        table.RunCommand.Execute(table.Rows[1]);
        Assert.Equal(0, runs);
        table.RunCommand.Execute(table.Rows[0]);
        Assert.Equal(1, runs);
        table.Write();
        var saved = JsonNode.Parse(doc.ToJson())!;
        var group = saved["hooks"]!["PreToolUse"]![0]!;
        Assert.Equal("  ^Read$  ", group["matcher"]!.GetValue<string>());
        Assert.Equal("group", group["extra"]!.GetValue<string>());
        Assert.Equal("handler", group["hooks"]![0]!["extra"]!.GetValue<string>());
        Assert.Equal("keep me", group["hooks"]![1]!["prompt"]!.GetValue<string>());
        Assert.Null(group["hooks"]![1]!["command"]);
        Assert.Equal(42, saved["unknown"]!.GetValue<int>());
    }

    [Fact]
    public void Reload_disconnects_old_rows_and_row_commands_preserve_order()
    {
        var doc = ClaudeSettingsDocument.TryParse("{}", out _)!;
        var changes = 0;
        var table = new HookTableViewModel { Changed = () => changes++ };
        table.Load(new ClaudeSettingsHookStore(doc));
        table.AddHookCommand.Execute("First");
        table.AddHookCommand.Execute("Second");
        var first = table.Rows[0];
        table.MoveDownCommand.Execute(first);
        Assert.Equal(["Second", "First"], table.Rows.Select(r => r.Event));
        table.Write();
        table.Load(new ClaudeSettingsHookStore(doc));
        var before = changes;
        first.Command = "detached";
        Assert.Equal(before, changes);
        var removed = table.Rows[0];
        table.SelectedRow = removed;
        table.RemoveCommand.Execute(removed);
        Assert.Null(table.SelectedRow);
        before = changes;
        removed.Command = "detached";
        Assert.Equal(before, changes);
        table.Write();
        Assert.Equal("First", Assert.Single(doc.GetHooks()).Event);
    }

    [Fact]
    public void Hook_test_pane_uses_product_supplied_samples_and_environment_without_running()
    {
        var profile = new HookTestProfile("fake", ["Custom"], (evt, matcher, cwd) => $"{evt}:{matcher}:{cwd}",
            _ => new Dictionary<string, string> { ["FAKE"] = "value" });
        var pane = new HookTestPaneViewModel { WorkingDirectory = @"C:\fake" };
        pane.Load("Custom", "match", "never run", profile);
        Assert.Equal(["Custom"], pane.Events);
        Assert.Equal(@"Custom:match:C:\fake", pane.Input);
        Assert.Equal("value", pane.Profile.Environment(@"C:\fake")["FAKE"]);
        Assert.False(pane.IsRunning);
        pane.Load("PreToolUse", "Bash", "never run");
        Assert.Equal("claude", pane.Profile.Product);
    }
}
