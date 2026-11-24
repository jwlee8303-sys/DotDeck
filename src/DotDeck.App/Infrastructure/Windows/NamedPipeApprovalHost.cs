using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using DotDeck.Core.AiCli.Approvals;
using DotDeck.Core.AiCli.Mcp;

namespace DotDeck.App.Infrastructure.Windows;

/// <summary>Receives what the approval hooks of one session report (called on pipe threads).</summary>
public interface IApprovalHookSink
{
    /// <summary>A PermissionRequest arrived. The sink answers (or releases) through <paramref name="connection"/>.</summary>
    void OnRequest(ClaudeApprovalHook.RequestMessage message, ApprovalHookConnection connection);

    /// <summary>A tool call ran (PostToolUse).</summary>
    void OnToolRan(string match);
}

/// <summary>
/// The answer channel of one waiting PermissionRequest hook. <see cref="Disconnected"/> fires when the hook process
/// ends on its own (Claude Code ends it when the prompt is rejected in the terminal).
/// </summary>
public sealed class ApprovalHookConnection : IApprovalChannel
{
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _write = new(1, 1);
    private int _closed;

    internal ApprovalHookConnection(StreamWriter writer) => _writer = writer;

    public event EventHandler? Disconnected;

    /// <summary>The hook still waits (no answer written, connection open).</summary>
    public bool IsOpen => Volatile.Read(ref _closed) == 0;

    public async Task<bool> SendAsync(ApprovalDecision decision, CancellationToken cancellationToken) =>
        await WriteOnceAsync(decision == ApprovalDecision.Allow ? ClaudeApprovalHook.BehaviorAllow : ClaudeApprovalHook.BehaviorDeny, cancellationToken).ConfigureAwait(false);

    public void Release() => _ = WriteOnceAsync(ClaudeApprovalHook.BehaviorNone, CancellationToken.None);

    internal void OnDisconnected()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    // One answer per connection: the first writer wins, later ones send nothing.
    private async Task<bool> WriteOnceAsync(string behavior, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return false;
        }

        await _write.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await JsonRpcFraming.WriteAsync(_writer, ClaudeApprovalHook.BuildDecisionMessage(behavior), timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            _write.Release();
        }
    }
}

/// <summary>
/// Approval channel of one interactive Claude session: a current-user-only pipe that the PermissionRequest and PostToolUse
/// hooks (<see cref="ClaudeApprovalHook"/>) connect to. Each connection authenticates with the session key (DPAPI-protected
/// in the session folder) before anything is read. Several hooks may wait at once.
/// </summary>
public sealed class NamedPipeApprovalHost : IDisposable
{
    private const int MaxInstances = 8;
    private readonly CancellationTokenSource _stop = new();
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly string _pipeName;
    private readonly string _marker;
    private readonly IApprovalHookSink _sink;
    private readonly Task _listening;
    private int _disposed;

    public NamedPipeApprovalHost(string directory, string sessionId, IApprovalHookSink sink)
    {
        if (!Guid.TryParseExact(sessionId, "N", out _)) throw new ArgumentException("Invalid session ID");
        _pipeName = PipeName(sessionId);
        _marker = Path.Combine(directory, ClaudeApprovalHook.PendingMarkerFile);
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        File.WriteAllBytes(Path.Combine(directory, ClaudeApprovalHook.KeyFile), new DpapiBackupProtector().Protect(_key));
        _listening = ListenAsync(CreatePipe());
    }

    /// <summary>Creates or removes the marker that makes PostToolUse hooks report (only while requests are open).</summary>
    public void SetPending(bool pending)
    {
        try
        {
            if (pending) File.WriteAllText(_marker, string.Empty);
            else File.Delete(_marker);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private NamedPipeServerStream CreatePipe() => new(_pipeName, PipeDirection.InOut, MaxInstances,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task ListenAsync(NamedPipeServerStream? first)
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = first ?? CreatePipe();
                first = null;
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                var connected = pipe;
                pipe = null;
                _ = Task.Run(() => HandleAsync(connected));
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // All instances busy, or stopping: wait a little and try again.
                pipe?.Dispose();
                await Task.Delay(TimeSpan.FromMilliseconds(200), _stop.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            ApprovalHookConnection? connection = null;
            try
            {
                using var input = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
                using var output = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true);
                using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                {
                    handshake.CancelAfter(TimeSpan.FromSeconds(5));
                    if (!Authenticate(await JsonRpcFraming.ReadAsync(input, handshake.Token).ConfigureAwait(false))) return;
                    await JsonRpcFraming.WriteAsync(output, "ok", handshake.Token).ConfigureAwait(false);
                    var line = await JsonRpcFraming.ReadAsync(input, handshake.Token).ConfigureAwait(false);
                    if (ClaudeApprovalHook.ReadToolRan(line) is { } match)
                    {
                        _sink.OnToolRan(match);
                        return;
                    }

                    if (ClaudeApprovalHook.ReadRequest(line) is not { } request) return;
                    connection = new ApprovalHookConnection(output);
                    try
                    {
                        _sink.OnRequest(request, connection);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        connection.Release(); // fail-safe: the CLI prompt decides
                    }
                }

                // The hook sends nothing more: a read that ends means the hook process is gone.
                while (await JsonRpcFraming.ReadAsync(input, _stop.Token).ConfigureAwait(false) is not null)
                {
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or DecoderFallbackException or InvalidDataException)
            {
            }
            finally
            {
                connection?.OnDisconnected();
            }
        }
    }

    private bool Authenticate(string? token)
    {
        byte[] candidate;
        try { candidate = Convert.FromBase64String(token ?? string.Empty); }
        catch (FormatException) { return false; }
        var valid = CryptographicOperations.FixedTimeEquals(candidate, _key);
        CryptographicOperations.ZeroMemory(candidate);
        return valid;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        SetPending(false);
        _stop.Cancel();
        _ = _listening.ContinueWith(_ => { CryptographicOperations.ZeroMemory(_key); _stop.Dispose(); }, TaskScheduler.Default);
    }

    private static string PipeName(string id) => "DotDeck.Approval." + id;

    /// <summary>
    /// <c>DotDeck.exe --dotdeck-approval &lt;sessionDir&gt;</c> (PermissionRequest hook). Always exits 0; prints a decision
    /// only when DotDeck answered allow or deny. Unreachable DotDeck, a closed connection, a timeout or any error prints
    /// nothing, so Claude Code's own prompt decides (fail-safe).
    /// </summary>
    public static async Task<int> RunRequestHookAsync(string[] args, Stream stdin, Stream stdout)
    {
        try
        {
            if (SessionOf(args, ClaudeApprovalHook.RequestSwitch) is not { } session) return 0;
            var message = ClaudeApprovalHook.BuildRequestMessage(ReadInput(stdin));
            if (message is null) return 0;
            using var lifetime = new CancellationTokenSource(ClaudeApprovalHook.MaxWait);
            await using var pipe = await ConnectAsync(session, lifetime.Token).ConfigureAwait(false);
            if (pipe is null) return 0;
            using var input = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
            using var output = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true);
            if (!await HandshakeAsync(session, input, output, lifetime.Token).ConfigureAwait(false)) return 0;
            await JsonRpcFraming.WriteAsync(output, message, lifetime.Token).ConfigureAwait(false);
            var behavior = ClaudeApprovalHook.ReadDecision(await JsonRpcFraming.ReadAsync(input, lifetime.Token).ConfigureAwait(false));
            if (ClaudeApprovalHook.HookOutput(behavior) is { } json)
            {
                var bytes = new UTF8Encoding(false).GetBytes(json);
                stdout.Write(bytes, 0, bytes.Length);
                stdout.Flush();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Fail-safe: no output.
        }

        return 0;
    }

    /// <summary><c>DotDeck.exe --dotdeck-approval-ran &lt;sessionDir&gt;</c> (PostToolUse hook). Always exits 0, never blocks.</summary>
    public static async Task<int> RunToolRanHookAsync(string[] args, Stream stdin)
    {
        try
        {
            if (SessionOf(args, ClaudeApprovalHook.ToolRanSwitch) is not { } session
                || !File.Exists(Path.Combine(session.Directory, ClaudeApprovalHook.PendingMarkerFile)))
            {
                return 0;
            }

            if (ClaudeApprovalHook.BuildToolRanMessage(ReadInput(stdin)) is not { } message) return 0;
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(ClaudeApprovalHook.ToolRanTimeoutSeconds - 2));
            await using var pipe = await ConnectAsync(session, lifetime.Token).ConfigureAwait(false);
            if (pipe is null) return 0;
            using var input = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
            using var output = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true);
            if (!await HandshakeAsync(session, input, output, lifetime.Token).ConfigureAwait(false)) return 0;
            await JsonRpcFraming.WriteAsync(output, message, lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }

        return 0;
    }

    private sealed record HookSession(string Directory, string Id);

    private static HookSession? SessionOf(string[] args, string expectedSwitch)
    {
        if (args.Length != 2 || args[0] != expectedSwitch || !Path.IsPathFullyQualified(args[1])) return null;
        var directory = Path.TrimEndingDirectorySeparator(args[1]);
        var id = Path.GetFileName(directory);
        return Guid.TryParseExact(id, "N", out _) && Directory.Exists(directory) ? new HookSession(directory, id) : null;
    }

    private static async Task<NamedPipeClientStream?> ConnectAsync(HookSession session, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", PipeName(session.Id), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connect.CancelAfter(ClaudeApprovalHook.ConnectTimeout);
            await pipe.ConnectAsync(connect.Token).ConfigureAwait(false);
            return pipe;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException or UnauthorizedAccessException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }
    }

    private static async Task<bool> HandshakeAsync(HookSession session, StreamReader input, StreamWriter output, CancellationToken cancellationToken)
    {
        var keyPath = Path.Combine(session.Directory, ClaudeApprovalHook.KeyFile);
        if (new FileInfo(keyPath).Length > 4096) return false;
        // Never read a redirected key file (including a redirected ancestor).
        for (var item = keyPath; item is not null; item = Path.GetDirectoryName(item))
            if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) return false;
        var key = new DpapiBackupProtector().Unprotect(File.ReadAllBytes(keyPath));
        try
        {
            if (key.Length != 32) return false;
            await JsonRpcFraming.WriteAsync(output, Convert.ToBase64String(key), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshake.CancelAfter(TimeSpan.FromSeconds(5));
        return await JsonRpcFraming.ReadAsync(input, handshake.Token).ConfigureAwait(false) == "ok";
    }

    private static string ReadInput(Stream stdin)
    {
        const int maxBytes = 4 * 1024 * 1024;
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = stdin.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > maxBytes) throw new InvalidDataException("훅 입력이 너무 큽니다");
        }

        var bytes = buffer.ToArray();
        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, offset, bytes.Length - offset);
    }
}
