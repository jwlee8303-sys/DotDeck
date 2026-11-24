using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using DotDeck.Core.Documents;
using DotDeck.Core.Files;
using DotDeck.Core.Paths;
using DotDeck.Core.Tests.Support;
using DotDeck.Core.Usage;

namespace DotDeck.Core.Tests.Usage;

public sealed class AgyStatusLineInstallerTests : IDisposable
{
    private const string Input = """{"conversationId":"conv-42","workspace":{"project_dir":"D:\\w"},"model":{"display_name":"Gemini 한글"},"context_window":{"used_percentage":38.5}}""";

    private readonly TempDirectory _temp = new();
    private readonly AppPaths _paths;
    private readonly AgyStatusLineInstaller _installer;
    private readonly string _backups;

    public AgyStatusLineInstallerTests()
    {
        _paths = new AppPaths(_temp.Combine("home"), HomeSource.CommandLine, _temp.Combine("roaming"), _temp.Combine("local"));
        _backups = _temp.Combine("backups");
        var store = PhysicalFileStore.Instance;
        var backups = new BackupStore(store, _backups, new FakeTimeProvider(DateTimeOffset.UtcNow));
        _installer = new AgyStatusLineInstaller(_paths, new TextFileReader(store), new SafeWriter(store, backups));
        Directory.CreateDirectory(Path.GetDirectoryName(_installer.SettingsPath)!);
    }

    public void Dispose() => _temp.Dispose();

    private JsonObject Settings() => (JsonObject)JsonNode.Parse(File.ReadAllText(_installer.SettingsPath))!;

    [Fact]
    public void Install_backs_up_settings_wraps_the_previous_command_and_keeps_other_keys()
    {
        const string previous = "node C:/Users/u/.gemini/hooks/handoff.cjs status";
        File.WriteAllText(_installer.SettingsPath, $$$"""{"theme":"dark","statusLine":{"type":"command","command":"{{{previous}}}","stack_with_default":true}}""");

        _installer.Install();

        var line = Settings()["statusLine"]!;
        Assert.Equal(_installer.ScriptCommand + " | " + previous, line["command"]!.GetValue<string>());
        Assert.True(line["stack_with_default"]!.GetValue<bool>());
        Assert.Equal("dark", Settings()["theme"]!.GetValue<string>());
        Assert.True(_installer.IsInstalled());
        Assert.Contains(Directory.EnumerateFiles(_backups, "*", SearchOption.AllDirectories),
            f => File.ReadAllText(f).Contains(previous, StringComparison.Ordinal)); // the original settings were backed up

        _installer.Uninstall();

        Assert.Equal(previous, Settings()["statusLine"]!["command"]!.GetValue<string>());
        Assert.True(Settings()["statusLine"]!["stack_with_default"]!.GetValue<bool>());
        Assert.False(File.Exists(_installer.ScriptPath));
    }

    [Fact]
    public void Pipeline_gives_the_previous_command_the_same_input_and_returns_its_output_and_exit_code()
    {
        _installer.Install();
        var previous = Path.Combine(_temp.Path, "previous.cmd");
        File.WriteAllText(previous, "@findstr /n \"^\"\r\n@exit /b 7\r\n");

        var (output, code) = RunPipeline(previous);

        Assert.Equal(7, code);
        Assert.Equal("1:" + Input, output.TrimEnd('\r', '\n'));
        var saved = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(_installer.ContextDir, "agy-conv-42.json")))!;
        Assert.Equal(38.5, saved["used_percentage"]!.GetValue<double>());
        Assert.Equal(@"D:\w", saved["cwd"]!.GetValue<string>());
        Assert.Equal("Gemini 한글", saved["model"]!.GetValue<string>());
    }

    [Fact]
    public void A_failing_save_still_runs_the_previous_command()
    {
        _installer.Install();
        File.WriteAllText(_installer.ContextDir, "not a folder"); // saving fails
        var previous = Path.Combine(_temp.Path, "previous.cmd");
        File.WriteAllText(previous, "@findstr /n \"^\"\r\n@exit /b 3\r\n");

        var (output, code) = RunPipeline(previous);

        Assert.Equal(3, code);
        Assert.Equal("1:" + Input, output.TrimEnd('\r', '\n'));
    }

    /// <summary>Runs "&lt;script&gt; | &lt;previous&gt;" the way a CLI's shell (cmd) runs the status line.</summary>
    private (string Output, int Code) RunPipeline(string previous)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        start.Arguments = "/d /s /c \"" + _installer.ScriptCommand + " | \"" + previous + "\"\""; // cmd quoting, not argv quoting
        using var process = Process.Start(start)!;
        process.StandardInput.Write(Input);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(60_000));
        return (output, process.ExitCode);
    }
}
