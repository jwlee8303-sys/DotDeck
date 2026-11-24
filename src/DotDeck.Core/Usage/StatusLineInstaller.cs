using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using DotDeck.Core.Documents;
using DotDeck.Core.Paths;

namespace DotDeck.Core.Usage;

/// <summary>
/// Connects a DotDeck PowerShell script in <c>~/.dotdeck</c> to a CLI's status line (Claude Code, agy). A previous
/// status line command keeps running: the new command is <c>&lt;script&gt; | &lt;previous command&gt;</c>, so the previous
/// command still runs in the CLI's own shell, receives the same input, and its output and exit code are the status
/// line's. The script passes its input on before saving anything and ignores its own failures. The previous
/// <c>statusLine</c> object is kept in <see cref="StatePath"/> for <see cref="Uninstall"/>. settings.json is written
/// through <see cref="SafeWriter"/> (backup first) and re-serialized: comments are dropped and the layout is normalized.
/// </summary>
public abstract class StatusLineInstaller(AppPaths paths, TextFileReader reader, SafeWriter writer)
{
    private const string Pipe = " | ";

    private static readonly JsonSerializerOptions s_write = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private static readonly JsonDocumentOptions s_read = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    protected AppPaths Paths { get; } = paths ?? throw new ArgumentNullException(nameof(paths));

    /// <summary>The CLI is set up on this PC (its home folder exists): connecting is offered only then.</summary>
    public virtual bool IsAvailable => true;

    /// <summary>The CLI's settings.json that holds <c>statusLine</c>.</summary>
    public abstract string SettingsPath { get; }

    /// <summary>"Claude Code", "agy" (messages).</summary>
    public abstract string ProductName { get; }

    protected abstract string ScriptFileName { get; }

    protected abstract string StateFileName { get; }

    /// <summary>The script's text (Windows PowerShell 5.1 compatible).</summary>
    protected abstract string ScriptText { get; }

    /// <summary>Files the script writes, deleted on <see cref="Uninstall"/> (besides the script and its state).</summary>
    protected abstract IEnumerable<string> SavedFiles { get; }

    public string ScriptPath => Path.Combine(Paths.DotDeckHomeDir, ScriptFileName);

    /// <summary>The <c>statusLine</c> object that was there before installing (JSON null when there was none).</summary>
    public string StatePath => Path.Combine(Paths.DotDeckHomeDir, StateFileName);

    /// <summary>Per-session context values the scripts save (<see cref="AiCli.ContextBudget.StatusLineContextStore"/>).</summary>
    public string ContextDir => Path.Combine(Paths.DotDeckHomeDir, "context");

    /// <summary>How the CLI starts the script (forward slashes: the path survives both cmd and bash quoting).</summary>
    public string ScriptCommand =>
        "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + ScriptPath.Replace('\\', '/') + "\"";

    /// <summary>True when the CLI's status line runs DotDeck's script.</summary>
    public bool IsInstalled()
    {
        var (_, root) = ReadSettings();
        return IsOurs(StatusLineCommand(root));
    }

    /// <summary>
    /// Rewrites an installed script when DotDeck ships a newer one (app start). Settings are never touched; nothing
    /// happens when the script is not there.
    /// </summary>
    /// <returns>True when the script file was rewritten.</returns>
    public bool RefreshScript()
    {
        if (!File.Exists(ScriptPath) || string.Equals(File.ReadAllText(ScriptPath), ScriptText, StringComparison.Ordinal))
        {
            return false;
        }

        WriteScript();
        return true;
    }

    /// <summary>Writes the script and points the status line at it, keeping the previous command behind a pipe.</summary>
    public void Install()
    {
        var (snapshot, root) = ReadSettings();
        var existing = root["statusLine"] as JsonObject;
        WriteScript();
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
    /// Removes DotDeck's script from the status line and deletes the script, its state and the saved values. A wrapped
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

        foreach (var file in new[] { ScriptPath, StatePath }.Concat(SavedFiles))
        {
            File.Delete(file);
        }
    }

    /// <summary>Saved context files of this product (<c>&lt;prefix&gt;-*.json</c> in <see cref="ContextDir"/>).</summary>
    protected IEnumerable<string> ContextFiles(string prefix) =>
        Directory.Exists(ContextDir) ? Directory.EnumerateFiles(ContextDir, prefix + "-*.json") : [];

    /// <summary>Written next to the script and moved over it: a CLI running the script never reads half a file.</summary>
    private void WriteScript()
    {
        Directory.CreateDirectory(Paths.DotDeckHomeDir);
        var temporary = ScriptPath + "." + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            File.WriteAllText(temporary, ScriptText, new UTF8Encoding(true));
            File.Move(temporary, ScriptPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
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
            : throw new InvalidDataException(ProductName + " settings.json is not a JSON object.");
    }

    private void Save(TextFileSnapshot? snapshot, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var plan = SafeWriter.Prepare(SettingsPath, snapshot, root.ToJsonString(s_write) + Environment.NewLine);
        if (!plan.IsUnchanged)
        {
            writer.Commit(plan);
        }
    }

    /// <summary>
    /// PowerShell shared by the scripts: <c>Save-Context</c> writes one session's context values (no conversation text)
    /// to <c>context\&lt;prefix&gt;-&lt;id&gt;.json</c> next to the script, atomically; ids other than letters, digits,
    /// '-' and '_' are not used as file names. <c>first_seen</c> (the session's first report) is kept from the previous
    /// file: DotDeck matches a session by folder only when it first reported after the session started.
    /// </summary>
    protected const string SaveContextFunction = """
        function Save-Context([string]$prefix, [string]$id, $values) {
            if (-not $id -or $id -notmatch '^[A-Za-z0-9_-]{1,100}$') { return }
            $dir = Join-Path (Split-Path -Parent $PSCommandPath) 'context'
            [void][IO.Directory]::CreateDirectory($dir)
            $path = Join-Path $dir ($prefix + '-' + $id + '.json')
            $first = $null
            if ([IO.File]::Exists($path)) { try { $first = ([IO.File]::ReadAllText($path, $utf8) | ConvertFrom-Json).first_seen } catch { } }
            if ($null -eq $first) { $first = $values['measured_at'] }
            $values['first_seen'] = $first
            $tmp = Join-Path $dir ($prefix + '-' + $id + '.' + $PID + '.tmp')
            [IO.File]::WriteAllText($tmp, (ConvertTo-Json $values -Compress -Depth 4), $utf8)
            Move-Item -LiteralPath $tmp -Destination $path -Force
        }
        """;
}

/// <summary>
/// DotDeck's Claude Code status line: saves the rate limits (<c>rate_limits.five_hour|seven_day</c>) to
/// <see cref="AppPaths.ClaudeUsageFile"/> and each session's context window values (<c>context_window</c>, model,
/// transcript path) to <c>~/.dotdeck/context/claude-&lt;session id&gt;.json</c>.
/// </summary>
public sealed class ClaudeStatusLineInstaller(AppPaths paths, TextFileReader reader, SafeWriter writer) : StatusLineInstaller(paths, reader, writer)
{
    public const string FileName = "claude-statusline.ps1";

    public override string SettingsPath => Path.Combine(Paths.ClaudeHome, "settings.json");

    public override string ProductName => "Claude Code";

    protected override string ScriptFileName => FileName;

    protected override string StateFileName => "claude-statusline.json";

    protected override string ScriptText => Script;

    protected override IEnumerable<string> SavedFiles => [Paths.ClaudeUsageFile, .. ContextFiles("claude")];

    /// <summary>
    /// Without <c>-Show</c> it passes its input on unchanged to the previous command (first, so a failure here never
    /// holds the previous command back); with it, it prints the model and the 5-hour usage itself.
    /// </summary>
    public const string Script = """
        # DotDeck: Claude Code status line. Installed and removed from DotDeck's AI monitor ("사용량" section).
        # Writes the input unchanged to stdout for the previous status line command (or, with -Show, prints a short
        # line itself), then saves the rate limits to claude-usage.json and the session's context window values to
        # context\claude-<session id>.json next to this script. Failures here are ignored.
        param([switch]$Show)
        $ErrorActionPreference = 'Stop'
        $utf8 = New-Object System.Text.UTF8Encoding $false
        [Console]::InputEncoding = $utf8
        [Console]::OutputEncoding = $utf8
        $text = [Console]::In.ReadToEnd()
        $data = $null
        try { $data = $text | ConvertFrom-Json } catch { }
        if ($Show) {
            $line = ''
            try {
                if ($data.model.display_name) { $line = [string]$data.model.display_name }
                $p = $data.rate_limits.five_hour.used_percentage
                if ($null -ne $p) { $line += ' | 5h ' + [math]::Round([double]$p) + '%' }
            } catch { }
            [Console]::Out.Write($line)
        } else {
            [Console]::Out.Write($text)
        }
        [Console]::Out.Flush()
        if (-not $data) { exit 0 }
        $dir = Split-Path -Parent $PSCommandPath
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
                $tmp = Join-Path $dir ('claude-usage.' + $PID + '.tmp')
                [IO.File]::WriteAllText($tmp, (ConvertTo-Json $out -Compress -Depth 4), $utf8)
                Move-Item -LiteralPath $tmp -Destination (Join-Path $dir 'claude-usage.json') -Force
            }
        } catch { }
        try {
        """ + "\n" + SaveContextFunction + "\n" + """
            $cw = $data.context_window
            if ($data.session_id -and $cw) {
                $cu = $cw.current_usage
                $used = $null
                if ($cu) { $used = [long]$cu.input_tokens + [long]$cu.cache_creation_input_tokens + [long]$cu.cache_read_input_tokens }
                Save-Context 'claude' ([string]$data.session_id) ([ordered]@{
                    session_id = [string]$data.session_id
                    transcript_path = $data.transcript_path
                    cwd = $data.cwd
                    model = $data.model.id
                    context_window_size = $cw.context_window_size
                    used_percentage = $cw.used_percentage
                    input_tokens = $used
                    measured_at = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
                })
            }
        } catch { }

        """;
}

/// <summary>
/// DotDeck's agy status line: saves each conversation's context use (<c>context_window.used_percentage</c>) to
/// <c>~/.dotdeck/context/agy-&lt;conversation id&gt;.json</c>. agy's own JSON fields are read defensively.
/// </summary>
public sealed class AgyStatusLineInstaller(AppPaths paths, TextFileReader reader, SafeWriter writer) : StatusLineInstaller(paths, reader, writer)
{
    public const string FileName = "agy-statusline.ps1";

    public override string SettingsPath => Path.Combine(Paths.AntigravityHome, "antigravity-cli", "settings.json");

    public override string ProductName => "agy";

    public override bool IsAvailable => Directory.Exists(Paths.AntigravityHome);

    protected override string ScriptFileName => FileName;

    protected override string StateFileName => "agy-statusline.json";

    protected override string ScriptText => Script;

    protected override IEnumerable<string> SavedFiles => ContextFiles("agy");

    /// <summary>Same order as Claude's script: input passed on (or a short line printed with <c>-Show</c>), then saved.</summary>
    public const string Script = """
        # DotDeck: agy status line. Installed and removed from DotDeck's AI monitor ("사용량" section).
        # Writes the input unchanged to stdout for the previous status line command (or, with -Show, prints a short
        # line itself), then saves the conversation's context use to context\agy-<id>.json next to this script.
        # Failures here are ignored.
        param([switch]$Show)
        $ErrorActionPreference = 'Stop'
        $utf8 = New-Object System.Text.UTF8Encoding $false
        [Console]::InputEncoding = $utf8
        [Console]::OutputEncoding = $utf8
        $text = [Console]::In.ReadToEnd()
        $data = $null
        try { $data = $text | ConvertFrom-Json } catch { }
        if ($Show) {
            $line = ''
            try {
                $p = $data.context_window.used_percentage
                if ($null -ne $p) { $line = 'ctx ' + [math]::Round([double]$p) + '%' }
            } catch { }
            [Console]::Out.Write($line)
        } else {
            [Console]::Out.Write($text)
        }
        [Console]::Out.Flush()
        if (-not $data) { exit 0 }
        try {
        """ + "\n" + SaveContextFunction + "\n" + """
            $id = $data.conversationId
            if (-not $id) { $id = $data.conversation_id }
            if (-not $id) { $id = $data.session_id }
            $cw = $data.context_window
            if ($id -and $cw -and -not $data.agentId -and -not $data.agent) {
                $cwd = $data.workspace.project_dir
                if (-not $cwd) { $cwd = $data.cwd }
                if (-not $cwd -and $data.workspacePaths) { $cwd = @($data.workspacePaths)[0] }
                $model = $data.model.id
                if (-not $model) { $model = $data.model.display_name }
                if (-not $model -and $data.model -is [string]) { $model = $data.model }
                Save-Context 'agy' ([string]$id) ([ordered]@{
                    session_id = [string]$id
                    cwd = $cwd
                    model = $model
                    context_window_size = $cw.context_window_size
                    used_percentage = $cw.used_percentage
                    measured_at = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
                })
            }
        } catch { }

        """;
}
