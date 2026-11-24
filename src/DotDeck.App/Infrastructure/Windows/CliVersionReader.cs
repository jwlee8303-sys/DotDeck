using System.Diagnostics;
using System.IO;

namespace DotDeck.App.Infrastructure.Windows;

/// <summary>Runs <c>&lt;cli&gt; --version</c> (no shell, no window, 5 s limit) to learn the installed CLI version.</summary>
public static class CliVersionReader
{
    public static async Task<string?> ReadAsync(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(executable))
        {
            return null;
        }

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        start.ArgumentList.Add("--version");
        using var process = Process.Start(start);
        if (process is null)
        {
            return null;
        }

        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return await output.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return null;
        }
    }
}
