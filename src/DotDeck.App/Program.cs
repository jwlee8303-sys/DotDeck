using System.IO;
using DotDeck.App.Infrastructure.Windows;
using DotDeck.Core.AiCli.Guard;

namespace DotDeck.App;

/// <summary>
/// Process entry. Helper modes used by AI CLI sessions (scope guard hook, heartbeat) run without starting WPF so they
/// stay fast and never show UI; everything else starts the application.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            switch (args[0])
            {
                case GuardHost.GuardSwitch:
                    return RunWithStdio(s => GuardHost.RunGuard(args, s.In, s.Out, s.Err, WindowsFinalPathResolver.Instance));
                case GuardHost.HeartbeatSwitch:
                    return RunWithStdio(s => GuardHost.RunHeartbeat(args, s.In, TimeProvider.System));
            }
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    private sealed record Stdio(Stream In, Stream Out, Stream Err);

    private static int RunWithStdio(Func<Stdio, int> run)
    {
        // A GUI-subsystem process still inherits the pipes its parent passed; Stream.Null when there are none.
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        using var error = Console.OpenStandardError();
        return run(new Stdio(input, output, error));
    }
}
