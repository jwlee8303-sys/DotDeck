namespace DotDeck.Installer;

/// <summary>
/// Command line. Users just double-click (no arguments). Administrators and tests may use
/// <c>--silent [--installto &lt;dir&gt;] [--autostart|--no-autostart] [--no-launch] [--log &lt;file&gt;]</c>.
/// <c>--elevated-install</c> is internal: the same EXE started with "runas" to do the machine-wide part.
/// </summary>
internal sealed class SetupOptions
{
    public bool Silent { get; private set; }
    public string? InstallTo { get; private set; }
    public bool? AutoStart { get; private set; }
    public bool NoLaunch { get; private set; }
    public string? Log { get; private set; }

    // Internal (elevated child).
    public bool ElevatedInstall { get; private set; }
    public string? Dir { get; private set; }
    public string? LegacyRoot { get; private set; }
    public string? MsiLog { get; private set; }
    public string? Status { get; private set; }
    public bool Quiet { get; private set; }

    public static SetupOptions Parse(IReadOnlyList<string> args)
    {
        var options = new SetupOptions();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string Value() => i + 1 < args.Count ? args[++i] : throw new ArgumentException(arg + " 옵션에 값이 필요합니다.");
            switch (arg.ToLowerInvariant())
            {
                case "--silent": options.Silent = true; break;
                case "--installto": options.InstallTo = Value(); break;
                case "--autostart": options.AutoStart = true; break;
                case "--no-autostart": options.AutoStart = false; break;
                case "--no-launch": options.NoLaunch = true; break;
                case "--log": options.Log = Value(); break;
                case "--elevated-install": options.ElevatedInstall = true; break;
                case "--dir": options.Dir = Value(); break;
                case "--legacy-root": options.LegacyRoot = Value(); break;
                case "--msi-log": options.MsiLog = Value(); break;
                case "--status": options.Status = Value(); break;
                case "--quiet": options.Quiet = true; break;
                default: throw new ArgumentException("알 수 없는 옵션: " + arg);
            }
        }

        return options;
    }
}

/// <summary>Process exit codes (Windows Installer values where one fits).</summary>
internal static class ExitCodes
{
    public const int Success = 0;
    public const int InvalidArguments = 87;
    public const int Cancelled = 1602;
    public const int Failed = 1603;
    public const int AppRunning = 32;
    public const int NewerVersionInstalled = 1638;
    public const int RebootRequired = 3010;
}
