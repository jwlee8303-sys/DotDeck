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

    /// <summary>File name without extension ("claude") and start time, or null when the process cannot be read.</summary>
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
        DateTimeOffset? started = GetProcessTimes(process, out var creation, out _, out _, out _) && creation > 0
            ? DateTimeOffset.FromFileTime(creation).ToUniversalTime()
            : null;
        return (name, started);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, char* name, ref int size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
}
