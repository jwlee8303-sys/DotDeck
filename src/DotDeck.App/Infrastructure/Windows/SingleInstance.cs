using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DotDeck.App.Infrastructure.Windows;

/// <summary>One UI per user and Windows session; CLI helper processes do not participate.</summary>
internal sealed partial class SingleInstance : IDisposable
{
    internal const string RestartSwitch = "--dotdeck-restart-parent";
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly SecurityIdentifier _user;
    private readonly CancellationTokenSource _stop = new();
    private Task? _listener;
    private bool _ownsMutex;

    internal SingleInstance(string? name = null)
    {
        using var identity = WindowsIdentity.GetCurrent();
        _user = identity.User ?? throw new InvalidOperationException("Windows 사용자 정보를 확인할 수 없습니다.");
        name ??= $"DotDeck.UI.{_user.Value}.{Process.GetCurrentProcess().SessionId}";
        _pipeName = name;
        var security = new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(_user, MutexRights.FullControl, AccessControlType.Allow));
        _mutex = MutexAcl.Create(false, @"Local\" + name, out _, security);
    }

    internal bool TryAcquire()
    {
        if (_ownsMutex) return true;
        try { _ownsMutex = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { _ownsMutex = true; }
        return _ownsMutex;
    }

    internal void Listen(Action activate)
    {
        if (!_ownsMutex || _listener is not null) throw new InvalidOperationException();
        // Create the endpoint before WPF starts; requests received during startup are queued by its dispatcher.
        _listener = ListenAsync(CreatePipe(), activate);
    }

    private NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(_user, PipeAccessRights.FullControl, AccessControlType.Allow));
        // An explicit SID ACL also permits the same user across normal/elevated launches.
        return NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    private async Task ListenAsync(NamedPipeServerStream? first, Action activate)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = first ?? CreatePipe();
                first = null;
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var request = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                request.CancelAfter(TimeSpan.FromSeconds(2));
                var data = new byte[1];
                if (await pipe.ReadAsync(data, request.Token).ConfigureAwait(false) == 1 && data[0] == 1)
                    activate();
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                // An interrupted client must not disable subsequent activation requests.
                await Task.Delay(50, _stop.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    internal async Task ActivateExistingAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        // Transfer foreground permission before asking the first process to activate its window.
        if (GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId))
            _ = AllowSetForegroundWindow(processId);
        await pipe.WriteAsync(new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
        await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
    }

    internal static string[] WaitForRestartParent(string[] args)
    {
        if (args.Length < 2 || args[0] != RestartSwitch) return args;
        if (!int.TryParse(args[1], out var processId) || processId <= 0 || processId == Environment.ProcessId)
            throw new InvalidOperationException("다시 시작할 프로세스가 올바르지 않습니다.");
        try
        {
            using var parent = Process.GetProcessById(processId);
            if (!parent.WaitForExit(30000))
                throw new InvalidOperationException("이전 DotDeck이 종료되지 않아 다시 시작하지 못했습니다.");
        }
        catch (ArgumentException) { /* The parent already exited. */ }
        return args[2..];
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            // The activation callback only queues UI work, so this cannot wait for the UI thread.
            // Close the pipe before releasing ownership to avoid racing the next launch.
            _listener?.GetAwaiter().GetResult();
        }
        finally
        {
            _stop.Dispose();
            if (_ownsMutex) _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint processId);
}
