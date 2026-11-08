using DotDeck.Core.Explorer;
using DotDeck.Core.Explorer.Visibility;
using DotDeck.Core.Projects;

namespace DotDeck.Core.Tests.Explorer;

public sealed class SecretAndFilterTests
{
    [Theory]
    [InlineData(".env")]
    [InlineData(".ENV")]
    [InlineData(".env.local")]
    [InlineData(".env.Production")]
    [InlineData(".envrc")]
    [InlineData("cert.pfx")]
    [InlineData("CERT.PEM")]
    [InlineData("server.key")]
    [InlineData("id_rsa")]
    [InlineData("id_rsa.pub")]
    [InlineData("ID_ED25519")]
    [InlineData("id_ed25519.pub")]
    [InlineData("cert.pem.")]
    [InlineData("cert.pem ")]
    [InlineData("cert.pem. . ")]
    [InlineData("cert.pem::$DATA")]
    [InlineData("server.key:stream")]
    [InlineData(".env.")]
    [InlineData(".env::$DATA")]
    [InlineData(@"C:\x\.env")]
    public void Secret_files_are_detected(string name)
    {
        Assert.True(SecretFilePolicy.IsSecret(name));
    }

    [Theory]
    [InlineData(".env.example")]
    [InlineData(".env.SAMPLE")]
    [InlineData(".env.template")]
    [InlineData(".env.production.example")]
    [InlineData("env.txt")]
    [InlineData("my.env")]
    [InlineData("keys.json")]
    [InlineData("monkey.txt")]
    [InlineData("settings.json")]
    [InlineData("readme_id_rsa.md")]
    public void Non_secret_files_are_not_flagged(string name)
    {
        Assert.False(SecretFilePolicy.IsSecret(name));
    }

    [Theory]
    [InlineData("CLAUDE.md", FileKind.Markdown)]
    [InlineData("settings.JSON", FileKind.Json)]
    [InlineData("config.toml", FileKind.Toml)]
    [InlineData("a.yml", FileKind.Yaml)]
    [InlineData("hook.ps1", FileKind.PowerShell)]
    [InlineData("Mod.psm1", FileKind.PowerShell)]
    [InlineData("x.py", FileKind.Python)]
    [InlineData("App.xaml", FileKind.Xaml)]
    [InlineData("Directory.Build.props", FileKind.MsBuild)]
    [InlineData("App.csproj", FileKind.MsBuild)]
    [InlineData("DotDeck.slnx", FileKind.Solution)]
    [InlineData("run.cmd", FileKind.Script)]
    [InlineData(".gitignore", FileKind.DotConfig)]
    [InlineData(".editorconfig", FileKind.DotConfig)]
    [InlineData(".env.local", FileKind.DotConfig)]
    [InlineData("id_rsa", FileKind.KeyMaterial)]
    [InlineData("photo.png", FileKind.Other)]
    [InlineData("Makefile", FileKind.Other)]
    [InlineData(".", FileKind.Other)]
    public void Files_are_classified_by_name(string name, FileKind expected)
    {
        Assert.Equal(expected, FileKindClassifier.Classify(name));
    }

    [Theory]
    [InlineData("CLAUDE.md")]
    [InlineData("README.MD")]
    [InlineData("settings.jsonc")]
    [InlineData("config.toml")]
    [InlineData("a.yaml")]
    [InlineData("notes.txt")]
    [InlineData("Directory.Packages.props")]
    [InlineData("App.csproj")]
    [InlineData("DotDeck.slnx")]
    [InlineData("setup.ini")]
    [InlineData("app.manifest.xml")]
    [InlineData("build.bat")]
    [InlineData("run.sh")]
    [InlineData("Tools.psm1")]
    [InlineData(".gitignore")]
    [InlineData(".editorconfig")]
    public void Migrated_configDocs_list_keeps_config_and_documents(string name)
    {
        Assert.True(ExtensionRules.Matches(name, ProjectsFileSerializer.LegacyConfigDocsExtensions));
    }

    /// <summary>Spec change (SPEC 2.3): extension-less dotfiles and .env* are no longer shown by default; check them one by one.</summary>
    [Theory]
    [InlineData(".npmrc")]
    [InlineData(".env")]
    [InlineData(".env.example")]
    public void Migrated_configDocs_list_no_longer_shows_other_dotfiles(string name)
    {
        Assert.False(ExtensionRules.Matches(name, ProjectsFileSerializer.LegacyConfigDocsExtensions));
    }

    [Theory]
    [InlineData("photo.png")]
    [InlineData("app.exe")]
    [InlineData("Makefile")]
    [InlineData("App.targets")]
    [InlineData(".eslintrc.cjs")]
    [InlineData("cert.pem")]
    public void Migrated_configDocs_list_hides_other_files(string name)
    {
        Assert.False(ExtensionRules.Matches(name, ProjectsFileSerializer.LegacyConfigDocsExtensions));
    }

    [Theory]
    [InlineData("photo.png")]
    [InlineData("Makefile")]
    [InlineData("cert.pem")]
    public void Show_all_files_passes_every_name(string name)
    {
        var evaluator = new VisibilityEvaluator(new VisibilitySettings { ShowAllFiles = true }, ExtensionRules.ConfigDocsPreset);
        Assert.True(evaluator.IsFileVisible(name));
    }
}
