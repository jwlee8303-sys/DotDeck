using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using DotDeck.Core.FileOperations;
using DotDeck.Core.Logging;

namespace DotDeck.App.Infrastructure.Windows;

/// <summary>
/// Recycle bin via <c>SHFileOperationW</c> (DESIGN 8: approved P/Invoke). Windows has no restore API, so after
/// recycling we locate the item's <c>$I</c>/<c>$R</c> pair in <c>&lt;drive&gt;\$Recycle.Bin\&lt;SID&gt;</c> and restore by
/// moving <c>$R</c> back (DESIGN Q7). DotDeck never deletes permanently on its own: drives without a recycle bin are
/// refused up front, and if the shell would still have to delete permanently (e.g. the item exceeds the recycle bin
/// size), <c>FOF_WANTNUKEWARNING</c> makes it ask the user first. Rejecting that case automatically needs an
/// IFileOperation progress sink (deferred).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsRecycleBin : IRecycleBin
{
    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;
    private const ushort FofWantNukeWarning = 0x4000;


    private readonly IAppLog _log;
    private readonly string? _sid = WindowsIdentity.GetCurrent().User?.Value;

    public WindowsRecycleBin(IAppLog log)
    {
        _log = log;
    }

    public bool CanRecycle(string path)
    {
        if (!Environment.Is64BitProcess)
        {
            // ShFileOpStruct below uses the 64-bit layout; refuse rather than risk a mis-marshalled delete.
            return false;
        }

        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // UNC paths (including \\?\ forms) have no recycle bin.
            return false;
        }

        try
        {
            if (new DriveInfo(root).DriveType != DriveType.Fixed)
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }

        var info = new ShQueryRbInfo { Size = Marshal.SizeOf<ShQueryRbInfo>() };
        return SHQueryRecycleBin(root, ref info) == 0;
    }

    public RecycleTicket Recycle(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var isDirectory = Directory.Exists(full);
        var before = SnapshotInfoFiles(full);

        // pFrom is a list of null-terminated strings ending with an extra null.
        var from = Marshal.StringToHGlobalUni(full + "\0\0");
        try
        {
            var op = new ShFileOpStruct
            {
                Func = FoDelete,
                From = from,
                Flags = (ushort)(FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi | FofWantNukeWarning),
            };
            var result = SHFileOperation(ref op);
            if (result != 0 || op.AnyOperationsAborted != 0 || Directory.Exists(full) || File.Exists(full))
            {
                throw new IOException($"휴지통으로 보내지 못했습니다(코드 0x{result:X}): {full}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(from);
        }

        var recycled = FindRecycled(full, isDirectory, before);
        if (recycled is null)
        {
            _log.Warning($"휴지통에서 항목 위치를 찾지 못했습니다: {full}");
        }

        return new RecycleTicket(full, isDirectory, recycled);
    }

    public bool Contains(RecycleTicket ticket) =>
        ticket.RecycledPath is { } recycled && (ticket.IsDirectory ? Directory.Exists(recycled) : File.Exists(recycled));

    public void Restore(RecycleTicket ticket)
    {
        if (ticket.RecycledPath is not { } recycled || !Contains(ticket))
        {
            throw new IOException($"휴지통에 항목이 없습니다: {ticket.OriginalPath}");
        }

        var original = ticket.OriginalPath;
        if (Directory.Exists(original) || File.Exists(original))
        {
            throw new IOException($"같은 이름의 항목이 이미 있습니다: {original}");
        }

        if (ticket.IsDirectory)
        {
            Directory.Move(recycled, original);
        }
        else
        {
            File.Move(recycled, original, overwrite: false);
        }

        // Remove the metadata so the recycle bin no longer lists the item.
        var infoFile = Path.Combine(Path.GetDirectoryName(recycled)!, "$I" + Path.GetFileName(recycled)[2..]);
        try
        {
            File.Delete(infoFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning($"휴지통 정보 파일을 지우지 못했습니다: {infoFile}", ex);
        }
    }

    /// <summary>Names of the $I files currently in the recycle bin folder of <paramref name="path"/>'s drive.</summary>
    private HashSet<string> SnapshotInfoFiles(string path)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (BinDirectoryOf(path) is { } binDirectory && Directory.Exists(binDirectory))
        {
            try
            {
                foreach (var infoFile in Directory.EnumerateFiles(binDirectory, "$I*"))
                {
                    names.Add(Path.GetFileName(infoFile));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warning($"휴지통 폴더를 읽지 못했습니다: {binDirectory}", ex);
            }
        }

        return names;
    }

    /// <summary>
    /// Finds the $R entry created by this delete: a $I file that did not exist before, records
    /// <paramref name="originalPath"/>, and has a matching $R of the right kind. Anything other than exactly one
    /// such entry (e.g. another program recycled the same path at the same moment) counts as "not found", so a
    /// restore can never pick up someone else's item.
    /// </summary>
    private string? FindRecycled(string originalPath, bool isDirectory, HashSet<string> before)
    {
        if (BinDirectoryOf(originalPath) is not { } binDirectory)
        {
            return null;
        }

        try
        {
            var matches = new List<string>();
            foreach (var infoFile in Directory.EnumerateFiles(binDirectory, "$I*"))
            {
                if (before.Contains(Path.GetFileName(infoFile)))
                {
                    continue;
                }

                var info = RecycleBinInfoFile.TryParse(File.ReadAllBytes(infoFile));
                if (info is null
                    || !string.Equals(Path.TrimEndingDirectorySeparator(info.OriginalPath), originalPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var dataPath = Path.Combine(binDirectory, "$R" + Path.GetFileName(infoFile)[2..]);
                if (isDirectory ? Directory.Exists(dataPath) : File.Exists(dataPath))
                {
                    matches.Add(dataPath);
                }
            }

            return matches.Count == 1 ? matches[0] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning($"휴지통 폴더를 읽지 못했습니다: {binDirectory}", ex);
            return null;
        }
    }

    private string? BinDirectoryOf(string path) =>
        _sid is not null && Path.GetPathRoot(path) is { Length: > 0 } root ? Path.Combine(root, "$Recycle.Bin", _sid) : null;
    // SHFILEOPSTRUCTW with default (natural) alignment, which matches the 64-bit SDK layout. DotDeck ships x64 only.
    [StructLayout(LayoutKind.Sequential)]
    private struct ShFileOpStruct
    {
        public IntPtr Hwnd;
        public uint Func;
        public IntPtr From;
        public IntPtr To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public IntPtr NameMappings;
        public IntPtr ProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShQueryRbInfo
    {
        public int Size;
        public long SizeInBytes;
        public long NumItems;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    private static partial int SHFileOperation(ref ShFileOpStruct operation);

    [LibraryImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHQueryRecycleBin(string rootPath, ref ShQueryRbInfo info);
}
