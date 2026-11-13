using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DotDeck.Installer;

/// <summary>
/// The machine-wide part, run elevated (the setup restarts itself with "runas", so the user sees one UAC prompt):
/// moves the program files of a per-user 1.5 install out of the way, runs the embedded MSI, and restores the old
/// files if the MSI fails. User data next to an old install (%LOCALAPPDATA%\DotDeck) is never moved or deleted.
/// </summary>
internal static class ElevatedInstall
{
    /// <summary>What Velopack's per-user setup put in its root; everything else there (settings, backups...) stays.</summary>
    private static readonly string[] LegacyProgramItems = ["current", "packages", "Update.exe", "DotDeck.exe", "sq.version", ".velopack_lock", ".betaId"];

    public static int Run(SetupOptions options)
    {
        if (options.Dir is null || options.MsiLog is null || options.Status is null || !Paths.IsLocalFullPath(options.Dir))
        {
            return ExitCodes.InvalidArguments;
        }

        var dir = Paths.Normalize(options.Dir);
        string? tempDirectory = null;
        string? backup = null;
        var legacyRoot = options.LegacyRoot is { } root && InstallState.IsLegacyRoot(Paths.Normalize(root)) ? Paths.Normalize(root) : null;
        try
        {
            tempDirectory = Path.Combine(Path.GetTempPath(), "DotDeck-Setup-" + Guid.NewGuid().ToString("N"));
            var msi = ExtractMsi(tempDirectory);
            if (msi is null)
            {
                return Fail(options.Status, ExitCodes.Failed, "설치 데이터가 포함되지 않은 설치 파일입니다. 배포 페이지에서 설치 파일을 다시 내려받으세요.");
            }

            if (legacyRoot is not null)
            {
                backup = MoveLegacyProgram(legacyRoot, moveEverything: Paths.SameFolder(legacyRoot, dir));
            }

            var arguments = new StringBuilder();
            arguments.Append("/i ").Append(Quote(msi));
            arguments.Append(" VELOPACK_INSTALLDIR=").Append(Quote(dir));
            if (MsiPackage.IsInstalled(msi))
            {
                // The same build again (repair): reinstall every file in place.
                arguments.Append(" REINSTALL=ALL REINSTALLMODE=vomus");
            }

            arguments.Append(" /norestart /l*v ").Append(Quote(options.MsiLog));
            arguments.Append(options.Quiet ? " /qn" : " /passive");

            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), arguments.ToString()) { UseShellExecute = false };
            using var process = Process.Start(start) ?? throw new IOException("msiexec를 시작하지 못했습니다.");
            process.WaitForExit();
            var code = process.ExitCode;
            if (code is ExitCodes.Success or ExitCodes.RebootRequired)
            {
                if (backup is not null) TryDeleteDirectory(backup);
                backup = null;
                return code;
            }

            return Fail(options.Status, code, code == ExitCodes.Cancelled ? "설치를 취소했습니다." : "Windows Installer 오류 " + code);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            return Fail(options.Status, ExitCodes.Failed, ex.Message);
        }
        finally
        {
            if (backup is not null && legacyRoot is not null) RestoreLegacyProgram(backup, legacyRoot);
            if (tempDirectory is not null) TryDeleteDirectory(tempDirectory);
        }
    }

    private static string MoveLegacyProgram(string root, bool moveEverything)
    {
        var backup = Path.Combine(Path.GetDirectoryName(root)!, "." + Path.GetFileName(root) + "-setup-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(backup);
        var names = moveEverything
            ? Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName).ToArray()
            : LegacyProgramItems;
        foreach (var name in names)
        {
            var source = Path.Combine(root, name);
            var target = Path.Combine(backup, name);
            if (Directory.Exists(source)) Directory.Move(source, target);
            else if (File.Exists(source)) File.Move(source, target);
        }

        return backup;
    }

    private static void RestoreLegacyProgram(string backup, string root)
    {
        try
        {
            Directory.CreateDirectory(root);
            foreach (var entry in Directory.EnumerateFileSystemEntries(backup).ToArray())
            {
                var target = Path.Combine(root, Path.GetFileName(entry));
                if (Directory.Exists(target) || File.Exists(target)) continue;
                if (Directory.Exists(entry)) Directory.Move(entry, target);
                else File.Move(entry, target);
            }

            TryDeleteDirectory(backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine(ex.Message); // The backup folder stays next to the old install for manual recovery.
        }
    }

    private static string? ExtractMsi(string directory)
    {
        using var resource = typeof(ElevatedInstall).Assembly.GetManifestResourceStream("DotDeck.msi");
        if (resource is null) return null;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "DotDeck.msi");
        using (var file = File.Create(path))
        {
            resource.CopyTo(file);
        }

        return path;
    }

    /// <summary>Paths never end with a backslash here (trimmed), so quoting needs no escaping.</summary>
    internal static string Quote(string value) => "\"" + value.Replace("\"", "") + "\"";

    private static int Fail(string statusFile, int code, string message)
    {
        try { File.WriteAllText(statusFile, message, Encoding.UTF8); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine(ex.Message); }
        return code == ExitCodes.Success ? ExitCodes.Failed : code;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine(ex.Message); }
    }
}

/// <summary>Reads the embedded MSI's ProductCode (msi.dll) to tell a repair of the same build from an upgrade.</summary>
internal static class MsiPackage
{
    private const int InstallStateDefault = 5;

    public static bool IsInstalled(string msiPath)
    {
        var productCode = ReadProperty(msiPath, "ProductCode");
        return productCode is not null && NativeMethods.MsiQueryProductStateW(productCode) == InstallStateDefault;
    }

    private static string? ReadProperty(string msiPath, string name)
    {
        if (NativeMethods.MsiOpenDatabaseW(msiPath, IntPtr.Zero, out var db) != 0) return null;
        try
        {
            if (NativeMethods.MsiDatabaseOpenViewW(db, "SELECT `Value` FROM `Property` WHERE `Property`='" + name + "'", out var view) != 0) return null;
            try
            {
                if (NativeMethods.MsiViewExecute(view, IntPtr.Zero) != 0 || NativeMethods.MsiViewFetch(view, out var record) != 0) return null;
                try
                {
                    uint length = 256;
                    var buffer = new StringBuilder((int)length);
                    return NativeMethods.MsiRecordGetStringW(record, 1, buffer, ref length) == 0 ? buffer.ToString() : null;
                }
                finally
                {
                    NativeMethods.MsiCloseHandle(record);
                }
            }
            finally
            {
                NativeMethods.MsiCloseHandle(view);
            }
        }
        finally
        {
            NativeMethods.MsiCloseHandle(db);
        }
    }

    private static class NativeMethods
    {
        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        public static extern uint MsiOpenDatabaseW(string path, IntPtr persist, out IntPtr handle);

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        public static extern uint MsiDatabaseOpenViewW(IntPtr database, string query, out IntPtr view);

        [DllImport("msi.dll")]
        public static extern uint MsiViewExecute(IntPtr view, IntPtr record);

        [DllImport("msi.dll")]
        public static extern uint MsiViewFetch(IntPtr view, out IntPtr record);

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        public static extern uint MsiRecordGetStringW(IntPtr record, uint field, StringBuilder buffer, ref uint length);

        [DllImport("msi.dll")]
        public static extern uint MsiCloseHandle(IntPtr handle);

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        public static extern int MsiQueryProductStateW(string productCode);
    }
}
