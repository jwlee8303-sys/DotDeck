using System.Reflection;

namespace DotDeck.Installer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // A single EXE has no .config to opt in: turn on the 4.7.1–4.8 UI Automation fixes (roles, names) here,
        // before any WinForms type reads the switches.
        AppContext.SetSwitch("Switch.UseLegacyAccessibilityFeatures", false);
        AppContext.SetSwitch("Switch.UseLegacyAccessibilityFeatures.2", false);
        AppContext.SetSwitch("Switch.UseLegacyAccessibilityFeatures.3", false);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        SetupOptions options;
        try
        {
            options = SetupOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message + "\n\n사용법: DotDeck-Setup.exe [--silent [--installto <폴더>] [--autostart|--no-autostart] [--no-launch] [--log <파일>]]",
                "DotDeck 설치", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return ExitCodes.InvalidArguments;
        }

        if (options.ElevatedInstall)
        {
            return ElevatedInstall.Run(options);
        }

        var engine = new SetupEngine(Metadata("PackId") ?? "DotDeck", Metadata("AppVersion") ?? "0.0.0");
        if (options.Log is not null) engine.LogPath = Path.GetFullPath(options.Log);
        return options.Silent ? RunSilent(engine, options) : RunWindow(engine);
    }

    private static int RunWindow(SetupEngine engine)
    {
        using var form = new InstallForm(engine);
        Application.Run(form);
        return form.ExitCode;
    }

    /// <summary>Same flow without the window: errors go to the log, the exit code tells the result.</summary>
    private static int RunSilent(SetupEngine engine, SetupOptions options)
    {
        var folder = engine.ResolveFolder(options.InstallTo ?? engine.DefaultFolder, _ => false, out var error);
        if (folder is null)
        {
            engine.Log("설치 폴더 오류: " + (error ?? "비어 있지 않은 폴더입니다."));
            return ExitCodes.InvalidArguments;
        }

        var problem = engine.CheckBeforeInstall(folder, out var code);
        if (problem is not null)
        {
            engine.Log(problem);
            return code;
        }

        var result = engine.Install(folder, options.AutoStart ?? engine.DefaultAutoStart, launch: !options.NoLaunch, quiet: true);
        if (result.Message is not null) engine.Log(result.Message);
        return result.ExitCode;
    }

    private static string? Metadata(string key) =>
        typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;
}
