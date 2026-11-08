using DotDeck.App.ViewModels.Editor;
using DotDeck.Core.Codex;
using DotDeck.Core.Catalogs;
using DotDeck.Core.Files;
using DotDeck.Core.Roles;
using DotDeck.Core.Editors;
using System.IO;

namespace DotDeck.App.Tests;

public sealed class CodexConfigFormTests
{
    private const string Sample = "# leading\r\nmodel = 'before' # keep\r\nfuture = 42\r\n\r\n[profiles.deep]\r\nmodel_reasoning_effort = 'future-level'\r\nunknown = 'preserve'\r\n[mcp_servers.test]\r\ncommand = 'demo'\r\nargs = ['first', 'second']\r\n[mcp_servers.test.env]\r\nAPI_KEY = 'fake-key-value-9371'\r\n[projects.'d:\\project']\r\ntrust_level = 'trusted'\r\n";
    [Fact]
    public void Secret_does_not_lock_any_field_and_unrelated_formatting_survives()
    {
        var writes = new List<string>();
        var form = new CodexConfigFormViewModel(writes.Add);
        Assert.Null(form.Load(Sample));
        Assert.Empty(writes);
        form.Models.First().Value = "after";
        Assert.Equal(Sample.Replace("'before'", "'after'", StringComparison.Ordinal), Assert.Single(writes));
        form.SelectedServer!.Env.Rows.Single().Value = "new-fake-key-9371";
        Assert.Equal(2, writes.Count);
        Assert.Contains("unknown = 'preserve'", writes[^1], StringComparison.Ordinal);
        Assert.Contains("# keep\r\nfuture = 42\r\n\r\n", writes[^1], StringComparison.Ordinal);
        Assert.Contains("new-fake-key-9371", writes[^1], StringComparison.Ordinal);
        Assert.Equal("future-level", form.ProfileFields.Single(f => f.Metadata.Key == "model_reasoning_effort").Value);
        Assert.NotNull(form.ProfileFields.Single(f => f.Metadata.Key == "model_reasoning_effort").Warning);
    }
    [Fact]
    public void Profile_and_server_operations_preserve_nested_unknown_keys()
    {
        var writes = new List<string>();
        var form = new CodexConfigFormViewModel(writes.Add);
        Assert.Null(form.Load(Sample));
        form.ProfileName = "renamed"; form.RenameProfileCommand.Execute(null);
        Assert.Contains("[profiles.renamed]", writes[^1], StringComparison.Ordinal);
        Assert.Contains("unknown = 'preserve'", writes[^1], StringComparison.Ordinal);
        form.ServerName = "renamed-server"; form.RenameServerCommand.Execute(null);
        Assert.Contains("[mcp_servers.renamed-server.env]", writes[^1], StringComparison.Ordinal);
        form.DeleteServerCommand.Execute(null);
        Assert.DoesNotContain("mcp_servers.", writes[^1], StringComparison.Ordinal);
        form.ProfileName = "new-profile"; form.AddProfileCommand.Execute(null);
        Assert.Contains("new-profile", form.Profiles);
        form.SelectedProfile = "renamed"; form.DeleteProfileCommand.Execute(null);
        Assert.DoesNotContain("unknown = 'preserve'", writes[^1], StringComparison.Ordinal);
    }
    [Fact]
    public void Catalog_override_and_automatic_config_kind_are_used()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotDeck-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Codex"));
        try
        {
            var json = CatalogResources.ReadBuiltIn(ConfigFormCatalog.Resource).Replace("사용할 모델 이름입니다.", "사용자 설명", StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(root, "Codex", "config-form.json"), json);
            var catalog = CatalogResources.Load(ConfigFormCatalog.Resource, ConfigFormCatalog.Parse, PhysicalFileStore.Instance, root);
            Assert.Equal(CatalogOrigin.Override, catalog.Origin);
            var form = new CodexConfigFormViewModel(_ => { }, catalog.Value);
            Assert.Null(form.Load("model = 'custom'"));
            Assert.Contains("사용자 설명", form.Models.First().Description, StringComparison.Ordinal);
            var resolver = new FileRoleResolver([FileRoleCatalog.LoadClaude()], PhysicalFileStore.Instance, @"C:\fake");
            Assert.Equal(EditorKind.CodexConfig, resolver.DefaultEditor(@"C:\fake\config.toml", null));
            Assert.NotNull(form.Load("model = ["));
        }
        finally { Directory.Delete(root, true); }
    }
}
