using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using DotDeck.Core.Documents;
using DotDeck.Core.Files;
using DotDeck.Core.Paths;
using DotDeck.Core.Tests.Support;
using DotDeck.Core.Usage;

namespace DotDeck.Core.Tests.Usage;

public sealed class ClaudeStatusLineInstallerTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppPaths _paths;
    private readonly ClaudeStatusLineInstaller _installer;

    public ClaudeStatusLineInstallerTests()
    {
        _paths = new AppPaths(_temp.Combine("home"), HomeSource.CommandLine, _temp.Combine("roaming"), _temp.Combine("local"));
        var store = PhysicalFileStore.Instance;
        var backups = new BackupStore(store, _temp.Combine("backups"), new FakeTimeProvider(DateTimeOffset.UtcNow));
        _installer = new ClaudeStatusLineInstaller(_paths, new TextFileReader(store), new SafeWriter(store, backups));
        Directory.CreateDirectory(_paths.ClaudeHome);
    }

    public void Dispose() => _temp.Dispose();

    private JsonObject Settings() => (JsonObject)JsonNode.Parse(File.ReadAllText(_installer.SettingsPath))!;

    [Fact]
    public void Install_keeps_the_previous_command_behind_a_pipe_and_uninstall_restores_it()
    {
        const string previous = "powershell.exe -NoProfile -File C:/tools/line.ps1";
        File.WriteAllText(_installer.SettingsPath, $$"""
            {
              // comment
              "model": "opus",
              "statusLine": { "type": "command", "command": "{{previous}}", "padding": 1 },
            }
            """);

        Assert.False(_installer.IsInstalled());
        _installer.Install();

        var line = Settings()["statusLine"]!;
        Assert.Equal(_installer.ScriptCommand + " | " + previous, line["command"]!.GetValue<string>());
        Assert.Equal(1, line["padding"]!.GetValue<int>());
        Assert.Equal("opus", Settings()["model"]!.GetValue<string>());
        Assert.True(File.Exists(_installer.ScriptPath));
        Assert.True(_installer.IsInstalled());

        _installer.Install(); // again: nothing is wrapped twice
        Assert.Equal(_installer.ScriptCommand + " | " + previous, Settings()["statusLine"]!["command"]!.GetValue<string>());

        File.WriteAllText(_paths.ClaudeUsageFile, "{}");
        _installer.Uninstall();

        Assert.Equal(previous, Settings()["statusLine"]!["command"]!.GetValue<string>());
        Assert.Equal(1, Settings()["statusLine"]!["padding"]!.GetValue<int>());
        Assert.False(_installer.IsInstalled());
        Assert.False(File.Exists(_installer.ScriptPath));
        Assert.False(File.Exists(_installer.StatePath));
        Assert.False(File.Exists(_paths.ClaudeUsageFile));
    }

    [Fact]
    public void Without_a_previous_status_line_the_script_shows_its_own_line_and_uninstall_removes_it()
    {
        _installer.Install(); // no settings.json yet

        Assert.Equal(_installer.ScriptCommand + " -Show", Settings()["statusLine"]!["command"]!.GetValue<string>());

        _installer.Uninstall();
        Assert.False(Settings().ContainsKey("statusLine"));
    }

    [Fact]
    public void Uninstall_keeps_changes_made_to_the_status_line_after_install()
    {
        File.WriteAllText(_installer.SettingsPath, """{"statusLine":{"type":"command","command":"echo old","padding":0}}""");
        _installer.Install();
        var settings = Settings();
        settings["statusLine"]!["command"] = _installer.ScriptCommand + " | echo edited";
        settings["statusLine"]!["padding"] = 2;
        File.WriteAllText(_installer.SettingsPath, settings.ToJsonString());

        _installer.Uninstall();

        Assert.Equal("echo edited", Settings()["statusLine"]!["command"]!.GetValue<string>());
        Assert.Equal(2, Settings()["statusLine"]!["padding"]!.GetValue<int>());
    }

    [Fact]
    public void Uninstall_of_the_script_alone_restores_a_status_line_that_ran_no_command()
    {
        File.WriteAllText(_installer.SettingsPath, """{"statusLine":{"type":"static","text":"hi"}}""");
        _installer.Install();
        Assert.Equal(_installer.ScriptCommand + " -Show", Settings()["statusLine"]!["command"]!.GetValue<string>());

        _installer.Uninstall();

        Assert.Equal("""{"type":"static","text":"hi"}""", Settings()["statusLine"]!.ToJsonString());
    }

    [Fact]
    public void Uninstall_without_its_state_file_restores_the_command_after_the_pipe()
    {
        File.WriteAllText(_installer.SettingsPath, """{"statusLine":{"type":"command","command":"echo hi"}}""");
        _installer.Install();
        File.Delete(_installer.StatePath);

        _installer.Uninstall();

        Assert.Equal("echo hi", Settings()["statusLine"]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void Script_saves_rate_limits_and_passes_its_input_through()
    {
        _installer.Install();
        const string input = """{"model":{"display_name":"Opus 한글"},"rate_limits":{"five_hour":{"used_percentage":72.4,"resets_at":1795440000},"seven_day":{"used_percentage":16,"resets_at":1795759200}}}""";

        var output = RunScript(input, show: false);

        Assert.Equal(input, output);
        var limits = ClaudeUsageReaderAccess.ReadLimits(_paths.ClaudeUsageFile)!;
        Assert.Equal(new LimitWindow(72.4, DateTimeOffset.FromUnixTimeSeconds(1795440000)), limits.FiveHour);
        Assert.Equal(16, limits.SevenDay!.UsedPercent);
        Assert.True(DateTimeOffset.UtcNow - limits.MeasuredAt < TimeSpan.FromMinutes(5));

        Assert.Equal("Opus 한글 | 5h 72%", RunScript(input, show: true));
    }

    private string RunScript(string input, bool show)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", _installer.ScriptPath })
        {
            start.ArgumentList.Add(arg);
        }

        if (show)
        {
            start.ArgumentList.Add("-Show");
        }

        using var process = Process.Start(start)!;
        process.StandardInput.Write(input);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(60_000));
        return output;
    }

    private static class ClaudeUsageReaderAccess
    {
        public static ServerLimits? ReadLimits(string path) => ClaudeUsageReader.ReadLimits(path);
    }
}
