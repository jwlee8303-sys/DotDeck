using System.Runtime.InteropServices;
using DotDeck.Core.AiCli.Scope;
using Microsoft.Win32.SafeHandles;

namespace DotDeck.App.Infrastructure.Windows;

/// <summary>
/// Resolves final paths with <c>GetFinalPathNameByHandle</c>: follows symbolic links and junctions, expands 8.3 short
/// names and returns on-disk casing. Mapped drives come back as UNC paths.
/// </summary>
public sealed partial class WindowsFinalPathResolver : IFinalPathResolver
{
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint OpenExisting = 3;
    private const uint ShareAll = 0x1 | 0x2 | 0x4;
    private const uint FileNameNormalized = 0x0;
    private const uint VolumeNameDos = 0x0;

    public static WindowsFinalPathResolver Instance { get; } = new();

    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    public PathExistence Probe(string fullPath)
    {
        // Attributes of the entry itself; a dangling link still "exists" and then fails to resolve.
        if (GetFileAttributesW(ToExtended(fullPath)) != uint.MaxValue)
        {
            return PathExistence.Exists;
        }

        // Only a definite "not found" lets the checker walk up; access denied and the like stay unknown.
        return Marshal.GetLastPInvokeError() is ErrorFileNotFound or ErrorPathNotFound
            ? PathExistence.Missing
            : PathExistence.Unknown;
    }

    public string? ResolveFinalPath(string fullPath)
    {
        using var handle = CreateFileW(ToExtended(fullPath), 0, ShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }

        var buffer = new char[512];
        while (true)
        {
            uint length;
            unsafe
            {
                fixed (char* p = buffer)
                {
                    length = GetFinalPathNameByHandleW(handle, p, (uint)buffer.Length, FileNameNormalized | VolumeNameDos);
                }
            }

            if (length == 0)
            {
                return null;
            }

            if (length < buffer.Length)
            {
                return ToDos(new string(buffer, 0, (int)length));
            }

            buffer = new char[length + 1];
        }
    }

    internal static string ToDos(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }

        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    private static string ToExtended(string fullPath)
    {
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return fullPath;
        }

        // Long paths: switch to the extended form so MAX_PATH does not apply.
        if (fullPath.Length < 248)
        {
            return fullPath;
        }

        return fullPath.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + fullPath[2..] : @"\\?\" + fullPath;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandleW(SafeFileHandle file, char* filePath, uint filePathLength, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileAttributesW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetFileAttributesW(string fileName);
}
