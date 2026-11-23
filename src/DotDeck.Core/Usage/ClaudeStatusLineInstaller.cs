using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using DotDeck.Core.Documents;
using DotDeck.Core.Paths;

namespace DotDeck.Core.Usage;

/// <summary>
/// Installs DotDeck's Claude Code status line: a PowerShell script in <c>~/.dotdeck</c> that saves the rate limits
/// Claude Code passes to status lines (<c>rate_limits.five_hour|seven_day</c>) to <see cref="AppPaths.ClaudeUsageFile"/>.
/// A previous status line command keeps running: the new command is <c>&lt;script&gt; | &lt;previous command&gt;</c>, so
/// the previous command still runs in Claude Code's own shell and receives the same input. The previous
/// <c>statusLine</c> object is kept in <see cref="StatePath"/> for <see cref="Uninstall"/>. settings.json is written
/// through <see cref="SafeWriter"/> (backup first) and re-serialized: comments are dropped and the layout is normalized.
/// </summary>
public sealed class ClaudeStatusLineInstaller(AppPaths paths, TextFileReader reader, SafeWriter writer)
{
    public const string ScriptFileName = "claude-statusline.ps1";

    private const string Pipe = " | ";

    private static readonly JsonSerializerOptions s_write = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private static readonly JsonDocumentOptions s_read = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public string SettingsPath => Path.Combine(paths.ClaudeHome, "settings.json");

    public string ScriptPath => Path.Combine(paths.DotDeckHomeDir, ScriptFileName);

    /// <summary>The <c>statusLine</c> object that was there before installing (JSON null when there was none).</summary>
    public string StatePath => Path.Combine(paths.DotDeckHomeDir, "claude-statusline.json");

    /// <summary>How Claude Code starts the script (forward slashes: the path survives both cmd and bash quoting).</summary>
    public string ScriptCommand =>
        "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + ScriptPath.Replace('\\', '/') + "\"";

    /// <summary>True when Claude Code's status line runs DotDeck's script.</summary>
    public bool IsInstalled()
    {
        var (_, root) = ReadSettings();
        return IsOurs(StatusLineCommand(root));
    }

    /// <summary>Writes the script and points Claude Code's status line at it, keeping the previous command behind a pipe.</summary>
    public void Install()
    {
        var (snapshot, root) = ReadSettings();
        var existing = root["statusLine"] as JsonObject;
        Directory.CreateDirectory(paths.DotDeckHomeDir);
        File.WriteAllText(ScriptPath, Script, new UTF8Encoding(true));
        if (IsOurs(StatusLineCommand(root)))
        {
            return; // already installed: the script is refreshed, settings stay
        }

        var state = new JsonObject { ["version"] = 1, ["original"] = existing?.DeepClone() };
        File.WriteAllText(StatePath, state.ToJsonString(s_write), new UTF8Encoding(false));

        var previous = existing is not null && (existing["type"]?.GetValue<string>() ?? "command") == "command"
            ? existing["command"]?.GetValue<string>()
            : null;
        var statusLine = existing?.DeepClone() as JsonObject ?? [];
        statusLine["type"] = "command";
        statusLine["command"] = string.IsNullOrWhiteSpace(previous) ? ScriptCommand + " -Show" : ScriptCommand + Pipe + previous;
        root["statusLine"] = statusLine;
        Save(snapshot, root);
    }

    /// <summary>
    /// Removes DotDeck's script from the status line and deletes the script, its state and the saved limits. A wrapped
    /// command keeps the current status line as it is now (later edits included) minus the script and the pipe; without
    /// one, the status line from before the install comes back (or none).
    /// </summary>
    public void Uninstall()
    {
        var (snapshot, root) = ReadSettings();
        var command = StatusLineCommand(root);
        if (IsOurs(command))
        {
            if (WrappedCommand(command!) is { } wrapped)
            {
                ((JsonObject)root["statusLine"]!)["command"] = wrapped;
            }
            else if (ReadOriginal() is (true, { } original))
            {
                root["statusLine"] = original;
            }
            else
            {
                root.Remove("statusLine");
            }

            Save(snapshot, root);
        }

        foreach (var file in new[] { ScriptPath, StatePath, paths.ClaudeUsageFile })
        {
            File.Delete(file);
        }
    }

    /// <summary>The command after "&lt;script&gt; | ", or null when the script runs alone (<c>-Show</c>).</summary>
    private string? WrappedCommand(string command)
    {
        var script = command.Replace('\\', '/').IndexOf(ScriptPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
        var pipe = script < 0 ? -1 : command.IndexOf(Pipe, script, StringComparison.Ordinal);
        return pipe < 0 || command[(pipe + Pipe.Length)..].Trim().Length == 0 ? null : command[(pipe + Pipe.Length)..];
    }

    private bool IsOurs(string? command) =>
        command is not null && command.Replace('\\', '/').Contains(ScriptPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static string? StatusLineCommand(JsonObject root) =>
        root["statusLine"] is JsonObject { } line && line["command"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>
    /// The saved previous <c>statusLine</c>: Known is false when the state file is missing or unreadable; otherwise
    /// Original is the object, or null when there was no status line before.
    /// </summary>
    private (bool Known, JsonObject? Original) ReadOriginal()
    {
        try
        {
            if (File.Exists(StatePath) && JsonNode.Parse(File.ReadAllText(StatePath)) is JsonObject state && state.ContainsKey("original"))
            {
                return (true, state["original"] is JsonObject original ? (JsonObject)original.DeepClone() : null);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unknown: the status line is removed.
        }

        return (false, null);
    }

    private (TextFileSnapshot? Snapshot, JsonObject Root) ReadSettings()
    {
        if (!File.Exists(SettingsPath))
        {
            return (null, []);
        }

        var snapshot = reader.Read(SettingsPath);
        if (snapshot.Text.Trim().Length == 0)
        {
            return (snapshot, []);
        }

        return JsonNode.Parse(snapshot.Text, documentOptions: s_read) is JsonObject root
            ? (snapshot, root)
            : throw new InvalidDataException("Claude settings.json is not a JSON object.");
    }

    private void Save(TextFileSnapshot? snapshot, JsonObject root)
    {
        Directory.CreateDirectory(paths.ClaudeHome);
        var plan = SafeWriter.Prepare(SettingsPath, snapshot, root.ToJsonString(s_write) + Environment.NewLine);
        if (!plan.IsUnchanged)
        {
            writer.Commit(plan);
        }
    }

    /// <summary>
    /// The status line script (Windows PowerShell 5.1 compatible). Without <c>-Show</c> it passes its input on
    /// unchanged to the previous command; with it, it prints the model and the 5-hour usage itself.
    /// </summary>
    public const string Script = """
        # DotDeck: Claude Code status line. Installed and removed from DotDeck's AI monitor ("사용량" section).
        # Saves the rate limits Claude Code passes on stdin to claude-usage.json next to this script, then writes the
        # input unchanged to stdout for the previous status line command (or, with -Show, prints a short line itself).
        param([switch]$Show)
        $ErrorActionPreference = 'Stop'
        $utf8 = New-Object System.Text.UTF8Encoding $false
        [Console]::InputEncoding = $utf8
        [Console]::OutputEncoding = $utf8
        $text = [Console]::In.ReadToEnd()
        $data = $null
        try { $data = $text | ConvertFrom-Json } catch { }
        try {
            $limits = $data.rate_limits
            if ($limits) {
                $out = [ordered]@{ measured_at = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() }
                foreach ($name in 'five_hour', 'seven_day') {
                    $w = $limits.$name
                    if ($w -and $null -ne $w.used_percentage) {
                        $out[$name] = [ordered]@{ used_percentage = $w.used_percentage; resets_at = $w.resets_at }
                    }
                }
                $dir = Split-Path -Parent $PSCommandPath
                $tmp = Join-Path $dir ('claude-usage.' + $PID + '.tmp')
                [IO.File]::WriteAllText($tmp, (ConvertTo-Json $out -Compress -Depth 4), $utf8)
                Move-Item -LiteralPath $tmp -Destination (Join-Path $dir 'claude-usage.json') -Force
            }
        } catch { }
        if ($Show) {
            $line = ''
            if ($data.model.display_name) { $line = [string]$data.model.display_name }
            $p = $data.rate_limits.five_hour.used_percentage
            if ($null -ne $p) { $line += ' | 5h ' + [math]::Round([double]$p) + '%' }
            [Console]::Out.Write($line)
        } else {
            [Console]::Out.Write($text)
        }

        """;
}
