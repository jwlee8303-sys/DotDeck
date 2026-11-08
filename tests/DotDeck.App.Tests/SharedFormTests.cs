using System.Text.Json.Nodes;
using DotDeck.App.ViewModels.Editor;
using DotDeck.Core.Editors;

namespace DotDeck.App.Tests;

public sealed class SharedFormTests
{
    [Fact]
    public void Environment_table_keeps_unknown_json_and_edits_once_per_action()
    {
        var writes = new List<string>();
        var form = new SettingsFormViewModel(writes.Add);
        Assert.Null(form.Load("""{"env":{"A":"one","B":"two","unknown":42},"other":{"keep":true}}"""));
        Assert.Empty(writes);
        form.Env[0].Value = "changed";
        var saved = JsonNode.Parse(Assert.Single(writes))!;
        Assert.Equal("changed", saved["env"]!["A"]!.GetValue<string>());
        Assert.Equal(42, saved["env"]!["unknown"]!.GetValue<int>());
        Assert.True(saved["other"]!["keep"]!.GetValue<bool>());
        writes.Clear();
        form.MoveDownCommand.Execute(form.Env[0]);
        Assert.Single(writes);
        Assert.Equal(["B", "A", "unknown"], form.Env.Select(r => r.Key));
        var old = form.Env[0];
        Assert.Null(form.Load("{}"));
        writes.Clear();
        old.Value = "detached";
        Assert.Empty(writes);
        form.AddEnvCommand.Execute(null);
        Assert.Single(writes);
    }

    [Fact]
    public void String_list_text_rows_and_commands_stay_in_sync_without_writes_on_load()
    {
        var changes = 0;
        var list = new StringListViewModel { Changed = () => changes++ };
        list.Load(["Read", "Write"]);
        Assert.Equal(0, changes);
        list.Text = " Read, , Edit ";
        Assert.Equal(1, changes);
        Assert.Equal(["Read", "Edit"], list.Values);
        list.Rows[1].Value = "Bash";
        Assert.Equal(2, changes);
        Assert.Equal("Read, Bash", list.Text);
        list.AddUnique("Bash");
        Assert.Equal(2, changes);
        list.AddUnique("Grep");
        Assert.Equal(3, changes);
        list.MoveUpCommand.Execute(list.Rows[2]);
        Assert.Equal(4, changes);
        Assert.Equal(["Read", "Grep", "Bash"], list.Values);
        var old = list.Rows[0];
        list.Load(["New"]);
        old.Value = "detached";
        Assert.Equal(4, changes);
        Assert.Equal("New", list.Text);
    }

    [Theory]
    [InlineData(EditorKind.Agent)]
    [InlineData(EditorKind.Skill)]
    public void Shared_frontmatter_tools_keep_single_undo_write_and_unknown_fields(EditorKind kind)
    {
        var writes = new List<string>();
        var form = new FrontmatterFormViewModel(kind, writes.Add);
        Assert.Same(FrontmatterProfiles.For(kind), form.Profile);
        Assert.Null(form.Load("---\nname: a\ndescription: d\ntools: [Read, Write]\nunknown: keep\n---\nbody"));
        Assert.Empty(writes);
        form.AddToolCommand.Execute("Read");
        Assert.Empty(writes);
        form.AddToolCommand.Execute("Bash");
        var saved = Assert.Single(writes);
        Assert.Equal(["Read", "Write", "Bash"], FrontmatterDocument.Parse(saved).GetList("tools"));
        Assert.Contains("unknown: keep", saved, StringComparison.Ordinal);
        writes.Clear();
        form.ToolList = string.Empty;
        Assert.Null(FrontmatterDocument.Parse(Assert.Single(writes)).Get("tools"));
    }

    [Fact]
    public void Another_frontmatter_profile_can_supply_its_own_fields_and_choices()
    {
        var profile = new FrontmatterProfile("fake", EditorKind.Skill, true, ["custom-model"], ["custom-tool"], (_, _) => []);
        var form = new FrontmatterFormViewModel(profile, _ => { });
        Assert.True(form.IsAgent);
        Assert.Equal(["custom-model"], form.Models);
        Assert.Equal(["custom-tool"], form.Tools);
    }
}
