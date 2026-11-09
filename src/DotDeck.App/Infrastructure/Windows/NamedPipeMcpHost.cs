using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotDeck.Core.AiCli.Mcp;
using DotDeck.Core.AiCli.Sessions;

namespace DotDeck.App.Infrastructure.Windows;

/// <summary>Local, current-user-only pipe. Each connection authenticates before any JSON-RPC is read.</summary>
public sealed class NamedPipeMcpHost : IDisposable
{
    public const string BridgeSwitch = "--dotdeck-mcp";
    public const string DirectoryVariable = "DOTDECK_MCP_DIRECTORY";
    public const string KeyFile = "bridge.key";
    public const string ConfigFile = "mcp.json";
    private readonly CancellationTokenSource _stop = new();
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly string _pipeName;
    private readonly IAiSessionHost _host;
    private readonly Task _listening;
    private int _disposed;

    public NamedPipeMcpHost(string directory, string sessionId, IAiSessionHost host)
    {
        if (!Guid.TryParseExact(sessionId, "N", out _)) throw new ArgumentException("Invalid session ID");
        _pipeName = PipeName(sessionId);
        _host = host;
        File.WriteAllBytes(Path.Combine(directory, KeyFile), new DpapiBackupProtector().Protect(_key));
        // Fail synchronously if the endpoint cannot be created, before launching a CLI that cannot connect.
        _listening = ListenAsync(CreatePipe());
    }

    public static string BuildConfig(string executable, string directory, string sessionId) => JsonSerializer.Serialize(new
    {
        mcpServers = new Dictionary<string, object>
        {
            [McpServer.ServerName] = new
            {
                type = "stdio", command = executable, args = new[] { BridgeSwitch, sessionId },
                env = new Dictionary<string, string> { [DirectoryVariable] = directory },
            },
        },
    });

    private NamedPipeServerStream CreatePipe() => new(_pipeName, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task ListenAsync(NamedPipeServerStream? first)
    {
        // Sequential connections prevent a local client from opening unbounded workers.
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = first ?? CreatePipe();
                first = null;
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var input = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
                using var output = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true);
                using var authentication = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                authentication.CancelAfter(TimeSpan.FromSeconds(5));
                var token = await JsonRpcFraming.ReadAsync(input, authentication.Token).ConfigureAwait(false);
                byte[] candidate;
                try { candidate = Convert.FromBase64String(token ?? string.Empty); }
                catch (FormatException) { continue; }
                var valid = CryptographicOperations.FixedTimeEquals(candidate, _key);
                CryptographicOperations.ZeroMemory(candidate);
                if (!valid) continue;
                await JsonRpcFraming.WriteAsync(output, "ok", _stop.Token).ConfigureAwait(false);
                await new McpServer(_host).RunAsync(input, output, _stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or DecoderFallbackException)
            {
                // Disconnected/invalid clients cannot stop the session's next connection.
                await Task.Delay(TimeSpan.FromMilliseconds(100), _stop.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        // Do not block a UI thread: a tool may be waiting to dispatch there.
        _ = _listening.ContinueWith(_ => { CryptographicOperations.ZeroMemory(_key); _stop.Dispose(); }, TaskScheduler.Default);
    }

    private static string PipeName(string id) => "DotDeck.Mcp." + id;

    public static async Task<int> RunBridgeAsync(string[] args, Stream stdin, Stream stdout)
    {
        if (args.Length != 2 || !Guid.TryParseExact(args[1], "N", out _)) return 2;
        var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (directory is null || !Path.IsPathFullyQualified(directory)
            || !string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)), args[1], StringComparison.Ordinal)) return 2;
        try
        {
            var keyPath = Path.Combine(directory, KeyFile);
            if (new FileInfo(keyPath).Length > 4096) return 2;
            // Never read a redirected key file (including a redirected ancestor).
            for (var item = keyPath; item is not null; item = Path.GetDirectoryName(item))
                if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) return 2;
            var key = new DpapiBackupProtector().Unprotect(File.ReadAllBytes(keyPath));
            if (key.Length != 32) return 2;
            using var pipe = new NamedPipeClientStream(".", PipeName(args[1]), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var lifetime = new CancellationTokenSource();
            await pipe.ConnectAsync(5000, lifetime.Token).ConfigureAwait(false);
            using var input = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
            using var output = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true);
            try { await JsonRpcFraming.WriteAsync(output, Convert.ToBase64String(key), lifetime.Token).ConfigureAwait(false); }
            finally { CryptographicOperations.ZeroMemory(key); }
            using var handshake = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (await JsonRpcFraming.ReadAsync(input, handshake.Token).ConfigureAwait(false) != "ok") return 2;
            using var clientInput = new StreamReader(stdin, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
            using var clientOutput = new StreamWriter(stdout, new UTF8Encoding(false), 4096, leaveOpen: true);
            var send = ForwardAsync(clientInput, output, lifetime.Token);
            var receive = ForwardAsync(input, clientOutput, lifetime.Token);
            var completed = await Task.WhenAny(send, receive).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
            lifetime.Cancel();
            // Console stdin may be a synchronous inherited handle. Do not keep a helper alive waiting for it
            // after the app has disconnected; process exit will close the inherited handles.
            var forwarding = Task.WhenAll(send, receive);
            try { await forwarding.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException) { }
            _ = forwarding.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or OperationCanceledException or ArgumentException)
        {
            return 2;
        }
    }

    private static async Task ForwardAsync(TextReader input, TextWriter output, CancellationToken cancellationToken)
    {
        while (await JsonRpcFraming.ReadAsync(input, cancellationToken).ConfigureAwait(false) is { } line)
            await JsonRpcFraming.WriteAsync(output, line, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Rejects a result if the scope changed or the session ended while the tool was running.</summary>
public sealed class LiveSessionHost(AiSession session, IAiSessionHost inner) : IAiSessionHost
{
    public async Task<JsonNode?> CallAsync(string tool, JsonObject arguments, CancellationToken cancellationToken)
    {
        if (session.State != AiSessionState.Running) throw new InvalidOperationException();
        var scope = session.Scope;
        var result = await inner.CallAsync(tool, arguments, cancellationToken).ConfigureAwait(false);
        if (session.State != AiSessionState.Running || !ReferenceEquals(scope, session.Scope)) throw new InvalidOperationException();
        return result;
    }
}
