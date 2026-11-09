using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using DotDeck.App.Infrastructure.Windows;
using DotDeck.Core.AiCli.Mcp;

namespace DotDeck.App.Tests;

public sealed class McpBridgeTests : IDisposable
{
    private readonly string _id = Guid.NewGuid().ToString("N");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dotdeck-mcp-tests-" + Guid.NewGuid().ToString("N"));
    private string SessionDirectory => Path.Combine(_root, _id);
    private sealed class Host : IAiSessionHost
    {
        public int Calls;
        public Task<JsonNode?> CallAsync(string tool, JsonObject arguments, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<JsonNode?>(new JsonObject { ["name"] = "한글 프로젝트" });
        }
    }

    public McpBridgeTests() => Directory.CreateDirectory(SessionDirectory);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    [Fact]
    public async Task Forged_authentication_never_reaches_the_tools()
    {
        var tools = new Host();
        using var host = new NamedPipeMcpHost(SessionDirectory, _id, tools);
        using var client = new NamedPipeClientStream(".", "DotDeck.Mcp." + _id, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(5000, TestContext.Current.CancellationToken);
        var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, new UTF8Encoding(false), leaveOpen: true);
        try
        {
            await writer.WriteLineAsync(Convert.ToBase64String(new byte[32]).AsMemory(), TestContext.Current.CancellationToken);
            try { Assert.Null(await reader.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)); }
            catch (IOException) { }
        }
        finally { try { writer.Dispose(); } catch (IOException) { } } // Authentication rejection closes the pipe.
        Assert.Equal(0, tools.Calls);
    }

    [Fact]
    public async Task WinExe_stdio_bridge_authenticates_and_round_trips_unicode_without_wpf()
    {
        var tools = new Host();
        using var host = new NamedPipeMcpHost(SessionDirectory, _id, tools);
        Assert.True(File.ReadAllBytes(Path.Combine(SessionDirectory, NamedPipeMcpHost.KeyFile)).Length > 32);
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "DotDeck.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false),
        };
        info.ArgumentList.Add(NamedPipeMcpHost.BridgeSwitch);
        info.ArgumentList.Add(_id);
        info.Environment[NamedPipeMcpHost.DirectoryVariable] = SessionDirectory;
        using var process = Process.Start(info)!;
        try
        {
            await process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}""".AsMemory(), TestContext.Current.CancellationToken);
            await process.StandardInput.FlushAsync(TestContext.Current.CancellationToken);
            var init = await process.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(1, JsonNode.Parse(init!)!["id"]!.GetValue<int>());
            await process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"list_projects"}}""".AsMemory(), TestContext.Current.CancellationToken);
            await process.StandardInput.FlushAsync(TestContext.Current.CancellationToken);
            var response = await process.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var text = JsonNode.Parse(response!)!["result"]!["content"]![0]!["text"]!.GetValue<string>();
            Assert.Equal("한글 프로젝트", JsonNode.Parse(text)!["name"]!.GetValue<string>());
            Assert.Equal(1, tools.Calls);
            process.StandardInput.Close();
            await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(TestContext.Current.CancellationToken); }
        }
    }
}
