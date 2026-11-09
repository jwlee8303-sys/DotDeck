using System.Collections;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DotDeck.Core.AiCli.Launch;
using DotDeck.Core.AiCli.Sessions;
using Microsoft.Win32.SafeHandles;

namespace DotDeck.App.Infrastructure.Windows;

/// <summary>
/// Starts CLI processes with <c>CreateProcessW</c> directly (no shell, no <c>ArgumentList</c> re-quoting) inside a new
/// Job (kill on close, breakaway not allowed). The job is attached at creation with
/// <c>PROC_THREAD_ATTRIBUTE_JOB_LIST</c>, so even a process that starts children immediately cannot escape it.
/// Pipe runs inherit only the three pipe ends (<c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c>); terminal runs inherit no
/// handles and are attached to a new pseudo console (<c>PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE</c>).
/// </summary>
public sealed partial class JobProcessLauncher : ICliProcessLauncher, ITerminalLauncher
{
    private const int PipeBufferSize = 64 * 1024;

    public ICliProcess Start(CliLaunchPlan plan)
    {
        Validate(plan);

        var job = CreateKillOnCloseJob();
        SafeFileHandle? stdinParent = null, stdinChild = null, stdoutParent = null, stdoutChild = null, stderrParent = null, stderrChild = null;
        try
        {
            CreatePipePair(childReads: true, inheritable: true, out stdinParent, out stdinChild);
            CreatePipePair(childReads: false, inheritable: true, out stdoutParent, out stdoutChild);
            CreatePipePair(childReads: false, inheritable: true, out stderrParent, out stderrChild);

            var info = CreateInJob(plan, job, [stdinChild, stdoutChild, stderrChild], console: null);
            var process = new JobProcess(job, info, stdoutParent, stderrParent);
            job = null;
            stdoutParent = null;
            stderrParent = null;

            var input = stdinParent;
            stdinParent = null;
            _ = WriteInputAsync(input, plan.StandardInput);
            return process;
        }
        finally
        {
            stdinChild?.Dispose();
            stdoutChild?.Dispose();
            stderrChild?.Dispose();
            stdinParent?.Dispose();
            stdoutParent?.Dispose();
            stderrParent?.Dispose();
            job?.Dispose();
        }
    }

    public ITerminalProcess Start(CliLaunchPlan plan, TerminalSize size)
    {
        Validate(plan);
        if (plan.StandardInput is not null)
        {
            throw new ArgumentException("터미널 실행에는 표준 입력 텍스트를 넘길 수 없습니다.", nameof(plan));
        }

        var job = CreateKillOnCloseJob();
        SafeFileHandle? inputParent = null, inputConsole = null, outputParent = null, outputConsole = null;
        SafePseudoConsoleHandle? console = null;
        try
        {
            // The console duplicates its ends, so ours are closed right after creation; nothing is inheritable.
            CreatePipePair(childReads: true, inheritable: false, out inputParent, out inputConsole);
            CreatePipePair(childReads: false, inheritable: false, out outputParent, out outputConsole);
            var result = CreatePseudoConsole(new COORD(size), inputConsole, outputConsole, 0, out console);
            if (result != 0)
            {
                throw new Win32Exception(result);
            }

            inputConsole.Dispose();
            inputConsole = null;
            outputConsole.Dispose();
            outputConsole = null;

            var info = CreateInJob(plan, job, inherit: null, console);
            var process = new TerminalProcess(job, info, console, inputParent, outputParent);
            job = null;
            console = null;
            inputParent = null;
            outputParent = null;
            return process;
        }
        finally
        {
            inputConsole?.Dispose();
            outputConsole?.Dispose();
            inputParent?.Dispose();
            outputParent?.Dispose();
            console?.Dispose();
            job?.Dispose();
        }
    }

    private static void Validate(CliLaunchPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ForbiddenOptions.Validate(plan);
        if (!Path.IsPathFullyQualified(plan.Executable) || !Path.IsPathFullyQualified(plan.WorkingDirectory))
        {
            throw new ArgumentException("실행 파일과 작업 폴더는 절대 경로여야 합니다.");
        }
    }

    private static async Task WriteInputAsync(SafeFileHandle handle, string? text)
    {
        // Off the caller's thread: a child that does not read stdin must never block the UI.
        await using var stream = new FileStream(handle, FileAccess.Write, 1, isAsync: false);
        if (!string.IsNullOrEmpty(text))
        {
            try
            {
                var bytes = new UTF8Encoding(false).GetBytes(text);
                await Task.Run(() => stream.Write(bytes)).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The child exited before reading its input.
            }
        }
    }

    private static SafeJobHandle CreateKillOnCloseJob()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
        };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            var error = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new Win32Exception(error);
        }

        return job;
    }

    private static void CreatePipePair(bool childReads, bool inheritable, out SafeFileHandle parent, out SafeFileHandle child)
    {
        var attributes = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = inheritable ? 1 : 0 };
        if (!CreatePipe(out var read, out var write, ref attributes, PipeBufferSize))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        (parent, child) = childReads ? (write, read) : (read, write);
        if (inheritable && !SetHandleInformation(parent, HANDLE_FLAG_INHERIT, 0))
        {
            var error = Marshal.GetLastPInvokeError();
            parent.Dispose();
            child.Dispose();
            throw new Win32Exception(error);
        }
    }

    /// <summary>
    /// Creates the process in <paramref name="job"/>, either with <paramref name="inherit"/> (stdin, stdout, stderr)
    /// as its only inherited handles or attached to <paramref name="console"/>.
    /// </summary>
    private static unsafe PROCESS_INFORMATION CreateInJob(
        CliLaunchPlan plan, SafeJobHandle job, SafeFileHandle[]? inherit, SafePseudoConsoleHandle? console)
    {
        if ((inherit is null) == (console is null))
        {
            throw new ArgumentException("inherit and console are exclusive");
        }

        var commandLine = (plan.CommandLine + '\0').ToCharArray();
        var environment = BuildEnvironmentBlock(plan.Environment).ToCharArray();

        var refs = new List<SafeHandle> { job };
        refs.AddRange(inherit ?? []);
        if (console is not null)
        {
            refs.Add(console);
        }

        var added = new bool[refs.Count];
        nint attributeList = 0;
        try
        {
            for (var i = 0; i < refs.Count; i++)
            {
                refs[i].DangerousAddRef(ref added[i]);
            }

            var handles = stackalloc nint[3];
            var jobs = stackalloc nint[1] { job.DangerousGetHandle() };

            nint size = 0;
            InitializeProcThreadAttributeList(0, 2, 0, ref size);
            attributeList = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributeList, 2, 0, ref size)
                || !UpdateProcThreadAttribute(attributeList, 0, PROC_THREAD_ATTRIBUTE_JOB_LIST, jobs, sizeof(nint), 0, 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            STARTUPINFOW startup;
            uint flags = EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT;
            if (inherit is not null)
            {
                for (var i = 0; i < 3; i++)
                {
                    handles[i] = inherit[i].DangerousGetHandle();
                }

                if (!UpdateProcThreadAttribute(attributeList, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handles, 3 * sizeof(nint), 0, 0))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                startup = new STARTUPINFOW { hStdInput = handles[0], hStdOutput = handles[1], hStdError = handles[2] };
                flags |= CREATE_NO_WINDOW;
            }
            else
            {
                // The attribute value is the HPCON itself. The standard handles are set explicitly invalid: otherwise a
                // child of a process whose own stdio is redirected (a test host, a service) can inherit those instead
                // of the console's.
                if (!UpdateProcThreadAttribute(attributeList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, (void*)console!.DangerousGetHandle(), sizeof(nint), 0, 0))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                startup = new STARTUPINFOW { hStdInput = -1, hStdOutput = -1, hStdError = -1 };
            }

            startup.cb = Marshal.SizeOf<STARTUPINFOEXW>();
            startup.dwFlags = STARTF_USESTDHANDLES;
            var startupEx = new STARTUPINFOEXW { StartupInfo = startup, lpAttributeList = attributeList };

            fixed (char* cmd = commandLine)
            fixed (char* env = environment)
            {
                if (!CreateProcessW(plan.Executable, cmd, 0, 0, inherit is not null, flags, env, plan.WorkingDirectory, ref startupEx, out var info))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                CloseHandle(info.hThread);
                info.hThread = 0;
                return info;
            }
        }
        finally
        {
            if (attributeList != 0)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }

            for (var i = 0; i < refs.Count; i++)
            {
                if (added[i])
                {
                    refs[i].DangerousRelease();
                }
            }
        }
    }

    /// <summary>The inherited environment plus the plan's additions, sorted case-insensitively as Windows requires.</summary>
    internal static string BuildEnvironmentBlock(IReadOnlyDictionary<string, string> additions)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value && key.Length > 0)
            {
                variables[key] = value;
            }
        }

        foreach (var (key, value) in additions)
        {
            if (key.Length == 0 || key.Contains('=', StringComparison.Ordinal) || key.Contains('\0', StringComparison.Ordinal) || value.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException($"환경 변수 이름 또는 값이 올바르지 않습니다: {key}");
            }

            variables[key] = value;
        }

        var block = new StringBuilder();
        foreach (var (key, value) in variables)
        {
            block.Append(key).Append('=').Append(value).Append('\0');
        }

        return block.Append('\0').ToString();
    }

    /// <summary>The job, the main process handle and its exit notification, shared by pipe and terminal runs.</summary>
    private abstract class JobProcessBase : IDisposable
    {
        private readonly SafeJobHandle _job;
        private readonly SafeWaitHandle _process;
        private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ProcessWait _wait;
        private RegisteredWaitHandle? _registration;
        private int _disposed;

        protected JobProcessBase(SafeJobHandle job, PROCESS_INFORMATION info)
        {
            _job = job;
            _process = new SafeWaitHandle(info.hProcess, ownsHandle: true);
            ProcessId = info.dwProcessId;
            _wait = new ProcessWait(_process);
        }

        public int ProcessId { get; }

        public Task<int> Exited => _exited.Task;

        /// <summary>Called by the derived constructor once its own state is ready for <see cref="OnExited"/>.</summary>
        protected void StartWaiting()
        {
            _registration = ThreadPool.RegisterWaitForSingleObject(_wait, (_, _) =>
            {
                int code;
                try
                {
                    code = GetExitCodeProcess(_process, out var exitCode) ? exitCode : -1;
                }
                catch (ObjectDisposedException)
                {
                    code = -1;
                }

                _exited.TrySetResult(code);
                OnExited();
            }, null, Timeout.Infinite, executeOnlyOnce: true);
        }

        /// <summary>Runs on a pool thread after the main process has exited (not after <see cref="Dispose"/>).</summary>
        protected virtual void OnExited()
        {
        }

        protected abstract void DisposeStreams();

        public unsafe IReadOnlyList<int> JobProcessIds()
        {
            // JOBOBJECT_BASIC_PROCESS_ID_LIST: DWORD assigned, DWORD listed, then ULONG_PTR ids (pointer aligned).
            const int capacity = 256;
            var buffer = stackalloc nint[1 + capacity];
            try
            {
                if (!QueryInformationJobObject(_job, JobObjectBasicProcessIdList, buffer, (1 + capacity) * sizeof(nint), 0))
                {
                    return [];
                }
            }
            catch (ObjectDisposedException)
            {
                return [];
            }

            var listed = (int)((uint*)buffer)[1];
            var ids = new int[Math.Min(listed, capacity)];
            for (var i = 0; i < ids.Length; i++)
            {
                ids[i] = (int)buffer[1 + i];
            }

            return ids;
        }

        public void Terminate(int exitCode = 1)
        {
            // The SafeHandle marshaller holds a reference for the call, so a concurrent Dispose cannot free the handle
            // underneath it; it can only make the call throw because the job is already closed (and thus ended).
            try
            {
                TerminateJobObject(_job, (uint)exitCode);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            // Closing the last job handle kills whatever is still running (KILL_ON_JOB_CLOSE).
            _job.Dispose();

            // Wait for a callback that is already running before the process handle goes away.
            if (_registration is { } registration)
            {
                using var unregistered = new ManualResetEvent(false);
                if (registration.Unregister(unregistered))
                {
                    unregistered.WaitOne();
                }
            }

            _exited.TrySetResult(-1);
            _wait.Dispose();
            _process.Dispose();
            DisposeStreams();
        }
    }

    private sealed class JobProcess : JobProcessBase, ICliProcess
    {
        public JobProcess(SafeJobHandle job, PROCESS_INFORMATION info, SafeFileHandle stdout, SafeFileHandle stderr)
            : base(job, info)
        {
            StandardOutput = new FileStream(stdout, FileAccess.Read, 4096, isAsync: false);
            StandardError = new FileStream(stderr, FileAccess.Read, 4096, isAsync: false);
            StartWaiting();
        }

        public Stream StandardOutput { get; }

        public Stream StandardError { get; }

        protected override void DisposeStreams()
        {
            StandardOutput.Dispose();
            StandardError.Dispose();
        }
    }

    private sealed class TerminalProcess : JobProcessBase, ITerminalProcess
    {
        private readonly SafePseudoConsoleHandle _console;

        public TerminalProcess(SafeJobHandle job, PROCESS_INFORMATION info, SafePseudoConsoleHandle console, SafeFileHandle input, SafeFileHandle output)
            : base(job, info)
        {
            _console = console;
            Input = new FileStream(input, FileAccess.Write, 1, isAsync: false);
            Output = new FileStream(output, FileAccess.Read, 4096, isAsync: false);
            StartWaiting();
        }

        public Stream Output { get; }

        public Stream Input { get; }

        public void Resize(TerminalSize size)
        {
            // The marshaller's reference keeps the console alive for the call; a closed console throws instead.
            try
            {
                _ = ResizePseudoConsole(_console, new COORD(size));
            }
            catch (ObjectDisposedException)
            {
            }
        }

        protected override void OnExited()
        {
            // The output pipe ends only when the console is closed. ClosePseudoConsole can wait for the console to
            // flush its last output into the pipe, so it runs on its own pool work item: Dispose waits for this exit
            // callback, and must still be able to reach Output.Dispose (which unblocks a flush nobody reads).
            ThreadPool.UnsafeQueueUserWorkItem(static console => console.Dispose(), _console, preferLocal: false);
        }

        protected override void DisposeStreams()
        {
            // Closing our read end first means a console flushing into a full pipe that nobody reads any more fails
            // instead of blocking ClosePseudoConsole on the caller's (possibly UI) thread.
            Output.Dispose();
            try
            {
                Input.Dispose();
            }
            catch (IOException)
            {
                // Flushing to a console that already ended.
            }

            _console.Dispose();
        }
    }

    private sealed class ProcessWait : WaitHandle
    {
        private readonly SafeWaitHandle _process;
        private int _added;

        public ProcessWait(SafeWaitHandle process)
        {
            var added = false;
            process.DangerousAddRef(ref added);
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
            _process = process;
            _added = added ? 1 : 0;
        }

        protected override void Dispose(bool explicitDisposing)
        {
            base.Dispose(explicitDisposing);
            if (Interlocked.Exchange(ref _added, 0) == 1)
            {
                _process.DangerousRelease();
            }
        }
    }

    internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    internal sealed class SafePseudoConsoleHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafePseudoConsoleHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle()
        {
            ClosePseudoConsole(handle);
            return true;
        }
    }

    // ---- Win32 ----

    private const int STARTF_USESTDHANDLES = 0x100;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const uint HANDLE_FLAG_INHERIT = 0x1;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectBasicProcessIdList = 3;
    private const int JobObjectExtendedLimitInformation = 9;
    private static readonly nint PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x00020002;
    private static readonly nint PROC_THREAD_ATTRIBUTE_JOB_LIST = 0x0002000D;
    private static readonly nint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;

        public COORD(TerminalSize size)
        {
            X = (short)size.Columns;
            Y = (short)size.Rows;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public nint lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOW
    {
        public int cb;
        public nint lpReserved;
        public nint lpDesktop;
        public nint lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEXW
    {
        public STARTUPINFOW StartupInfo;
        public nint lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeJobHandle CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(SafeJobHandle job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryInformationJobObject(SafeJobHandle job, int infoClass, void* info, int length, nint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(SafeJobHandle job, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SECURITY_ATTRIBUTES attributes, int size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, void* value, nint size, nint previous, nint returnSize);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(nint list);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcessW(
        string applicationName, char* commandLine, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, char* environment, string currentDirectory,
        ref STARTUPINFOEXW startupInfo, out PROCESS_INFORMATION processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(SafeWaitHandle process, out int exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial int CreatePseudoConsole(COORD size, SafeFileHandle input, SafeFileHandle output, uint flags, out SafePseudoConsoleHandle console);

    [LibraryImport("kernel32.dll")]
    private static partial int ResizePseudoConsole(SafePseudoConsoleHandle console, COORD size);

    [LibraryImport("kernel32.dll")]
    private static partial void ClosePseudoConsole(nint console);
}
