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
/// <c>PROC_THREAD_ATTRIBUTE_JOB_LIST</c>, so even a process that starts children immediately cannot escape it, and only
/// the three pipe ends are inherited (<c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c>).
/// </summary>
public sealed partial class JobProcessLauncher : ICliProcessLauncher
{
    private const int PipeBufferSize = 64 * 1024;

    public ICliProcess Start(CliLaunchPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ForbiddenOptions.Validate(plan);
        if (!Path.IsPathFullyQualified(plan.Executable) || !Path.IsPathFullyQualified(plan.WorkingDirectory))
        {
            throw new ArgumentException("실행 파일과 작업 폴더는 절대 경로여야 합니다.");
        }

        var job = CreateKillOnCloseJob();
        SafeFileHandle? stdinParent = null, stdinChild = null, stdoutParent = null, stdoutChild = null, stderrParent = null, stderrChild = null;
        try
        {
            CreatePipePair(childReads: true, out stdinParent, out stdinChild);
            CreatePipePair(childReads: false, out stdoutParent, out stdoutChild);
            CreatePipePair(childReads: false, out stderrParent, out stderrChild);

            var info = CreateInJob(plan, job, stdinChild, stdoutChild, stderrChild);
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

    private static void CreatePipePair(bool childReads, out SafeFileHandle parent, out SafeFileHandle child)
    {
        var attributes = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = 1 };
        if (!CreatePipe(out var read, out var write, ref attributes, PipeBufferSize))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        (parent, child) = childReads ? (write, read) : (read, write);
        if (!SetHandleInformation(parent, HANDLE_FLAG_INHERIT, 0))
        {
            var error = Marshal.GetLastPInvokeError();
            parent.Dispose();
            child.Dispose();
            throw new Win32Exception(error);
        }
    }

    private static unsafe PROCESS_INFORMATION CreateInJob(
        CliLaunchPlan plan, SafeJobHandle job, SafeFileHandle stdin, SafeFileHandle stdout, SafeFileHandle stderr)
    {
        var commandLine = (plan.CommandLine + '\0').ToCharArray();
        var environment = BuildEnvironmentBlock(plan.Environment).ToCharArray();

        var refs = new SafeHandle[] { job, stdin, stdout, stderr };
        var added = new bool[refs.Length];
        nint attributeList = 0;
        try
        {
            for (var i = 0; i < refs.Length; i++)
            {
                refs[i].DangerousAddRef(ref added[i]);
            }

            var inherit = stackalloc nint[3] { stdin.DangerousGetHandle(), stdout.DangerousGetHandle(), stderr.DangerousGetHandle() };
            var jobs = stackalloc nint[1] { job.DangerousGetHandle() };

            nint size = 0;
            InitializeProcThreadAttributeList(0, 2, 0, ref size);
            attributeList = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributeList, 2, 0, ref size)
                || !UpdateProcThreadAttribute(attributeList, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, inherit, 3 * sizeof(nint), 0, 0)
                || !UpdateProcThreadAttribute(attributeList, 0, PROC_THREAD_ATTRIBUTE_JOB_LIST, jobs, sizeof(nint), 0, 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            var startup = new STARTUPINFOEXW
            {
                StartupInfo = new STARTUPINFOW
                {
                    cb = Marshal.SizeOf<STARTUPINFOEXW>(),
                    dwFlags = STARTF_USESTDHANDLES,
                    hStdInput = inherit[0],
                    hStdOutput = inherit[1],
                    hStdError = inherit[2],
                },
                lpAttributeList = attributeList,
            };

            fixed (char* cmd = commandLine)
            fixed (char* env = environment)
            {
                if (!CreateProcessW(plan.Executable, cmd, 0, 0, true,
                        EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW,
                        env, plan.WorkingDirectory, ref startup, out var info))
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

            for (var i = 0; i < refs.Length; i++)
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

    private sealed class JobProcess : ICliProcess
    {
        private readonly SafeJobHandle _job;
        private readonly SafeWaitHandle _process;
        private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly RegisteredWaitHandle _registration;
        private readonly ProcessWait _wait;

        public JobProcess(SafeJobHandle job, PROCESS_INFORMATION info, SafeFileHandle stdout, SafeFileHandle stderr)
        {
            _job = job;
            _process = new SafeWaitHandle(info.hProcess, ownsHandle: true);
            ProcessId = info.dwProcessId;
            StandardOutput = new FileStream(stdout, FileAccess.Read, 4096, isAsync: false);
            StandardError = new FileStream(stderr, FileAccess.Read, 4096, isAsync: false);
            _wait = new ProcessWait(_process);
            _registration = ThreadPool.RegisterWaitForSingleObject(_wait, (_, _) =>
            {
                try
                {
                    _exited.TrySetResult(GetExitCodeProcess(_process, out var code) ? code : -1);
                }
                catch (ObjectDisposedException)
                {
                    _exited.TrySetResult(-1);
                }
            }, null, Timeout.Infinite, executeOnlyOnce: true);
        }

        public int ProcessId { get; }

        public Stream StandardOutput { get; }

        public Stream StandardError { get; }

        public Task<int> Exited => _exited.Task;

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
            using (var unregistered = new ManualResetEvent(false))
            {
                if (_registration.Unregister(unregistered))
                {
                    unregistered.WaitOne();
                }
            }

            _exited.TrySetResult(-1);
            _wait.Dispose();
            _process.Dispose();
            StandardOutput.Dispose();
            StandardError.Dispose();
        }

        private int _disposed;
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
}
