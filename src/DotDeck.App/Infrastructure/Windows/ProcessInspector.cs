using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DotDeck.App.Infrastructure.Windows;

/// <summary>
/// Executable name and start time of a process, read with limited query rights (no full process snapshot). The
/// "AI 모니터" uses it to find AI CLIs that were started by hand in a terminal tab.
/// </summary>
public static partial class ProcessInspector
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessCommandLineInformation = 60;
    private const int MaxCommandLineBytes = 32768 * sizeof(char);

    /// <summary>Full path of the process's executable, or null when it cannot be read.</summary>
    public static unsafe string? ImagePath(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        using var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (process.IsInvalid)
        {
            return null;
        }

        const int capacity = 1024;
        var buffer = stackalloc char[capacity];
        var length = capacity;
        return QueryFullProcessImageNameW(process, 0, buffer, ref length) && length > 0 ? new string(buffer, 0, length) : null;
    }

    /// <summary>File name and start time, or null for an unreadable process or a Codex background server.</summary>
    public static unsafe (string Name, DateTimeOffset? StartedAt)? Inspect(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        using var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (process.IsInvalid)
        {
            return null;
        }

        const int capacity = 1024;
        var buffer = stackalloc char[capacity];
        var length = capacity;
        if (!QueryFullProcessImageNameW(process, 0, buffer, ref length) || length <= 0)
        {
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(new string(buffer, 0, length));
        if (name.Equals("codex", StringComparison.OrdinalIgnoreCase) && IsCodexServer(process))
        {
            return null;
        }

        DateTimeOffset? started = GetProcessTimes(process, out var creation, out _, out _, out _) && creation > 0
            ? DateTimeOffset.FromFileTime(creation).ToUniversalTime()
            : null;
        return (name, started);
    }

    // The Codex CLI and its app-server use the same executable. A server can outlive the interactive CLI.
    // Read arguments only for Codex and keep them local. If the native query fails, retain name-based detection.
    private static unsafe bool IsCodexServer(SafeProcessHandle process)
    {
        _ = NtQueryInformationProcess(process, ProcessCommandLineInformation, null, 0, out var capacity);
        if (capacity <= sizeof(UnicodeString) || capacity > sizeof(UnicodeString) + MaxCommandLineBytes)
        {
            return false;
        }

        var buffer = new byte[capacity];
        fixed (byte* data = buffer)
        {
            if (NtQueryInformationProcess(process, ProcessCommandLineInformation, data, capacity, out _) < 0)
            {
                return false;
            }

            var commandLine = (UnicodeString*)data;
            var text = commandLine->Buffer;
            if (text != (char*)(data + sizeof(UnicodeString))
                || commandLine->Length > capacity - sizeof(UnicodeString) - sizeof(char)
                || commandLine->Length % sizeof(char) != 0
                || text[commandLine->Length / sizeof(char)] != '\0')
            {
                return false;
            }

            var argv = CommandLineToArgvW(text, out var argc);
            if (argv is null)
            {
                return false;
            }

            try
            {
                return argc > 1 && new string(argv[1]) is "app-server" or "mcp-server";
            }
            finally
            {
                _ = LocalFree((nint)argv);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public char* Buffer;
    }

    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtQueryInformationProcess(
        SafeProcessHandle process, int informationClass, void* buffer, int bufferSize, out int returnLength);

    [LibraryImport("shell32.dll")]
    private static unsafe partial char** CommandLineToArgvW(char* commandLine, out int argc);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, char* name, ref int size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
}
