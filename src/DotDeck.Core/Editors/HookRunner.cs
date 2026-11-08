using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;

namespace DotDeck.Core.Editors;

public sealed record HookRunResult(int? ExitCode, string StandardOutput, string StandardError, TimeSpan Elapsed, bool TimedOut)
{
    /// <summary>Claude Code reads exit code 2 as "block" (stderr is fed back to the model).</summary>
    public string Meaning => TimedOut ? "시간 초과" : ExitCode switch
    {
        0 => "성공",
        2 => "차단(종료 코드 2)",
        null => "종료 코드 없음",
        _ => "오류(차단하지 않음)",
    };
}

/// <summary>
/// Runs a hook command the way Claude Code does: through the shell, with the event JSON on stdin (SPEC §7, hook
/// test runner). Output is capped so a chatty script cannot exhaust memory.
/// </summary>
public static class HookRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    public const int MaxOutputChars = 200_000;

    public static async Task<HookRunResult> RunAsync(
        string command, string stdin, string workingDirectory, TimeSpan timeout, IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(stdin);
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comSpec ? comSpec : "cmd.exe",
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };

        // cmd /s /c "<command>": cmd strips the outer quotes and runs the command line as written. Arguments is
        // passed verbatim (ArgumentList would backslash-escape inner quotes, which cmd does not understand).
        start.Arguments = $"/d /s /c \"{command}\"";
        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[key] = value;
        }

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = start };
        process.Start();
        var stdout = ReadCappedAsync(process.StandardOutput);
        var stderr = ReadCappedAsync(process.StandardError);

        // The time limit starts with the process and also covers writing stdin (a hook that never reads it would
        // otherwise block the write once the pipe is full).
        var timedOut = false;
        using (var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timer.CancelAfter(timeout);
            try
            {
                try
                {
                    await process.StandardInput.WriteAsync(stdin.AsMemory(), timer.Token).ConfigureAwait(false);
                    process.StandardInput.Close();
                }
                catch (IOException)
                {
                    // The command exited without reading stdin.
                }

                await process.WaitForExitAsync(timer.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                timedOut = !cancellationToken.IsCancellationRequested;
                Kill(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        // A background child that inherited the pipes can keep them open after the shell exits; do not wait for it.
        var output = Task.WhenAll(stdout, stderr);
        if (await Task.WhenAny(output, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None)).ConfigureAwait(false) != output)
        {
            stopwatch.Stop();
            return new HookRunResult(timedOut ? null : process.ExitCode, "(출력 대기 시간 초과: 백그라운드 프로세스가 파이프를 잡고 있습니다)", string.Empty, stopwatch.Elapsed, timedOut);
        }

        stopwatch.Stop();
        return new HookRunResult(timedOut ? null : process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false), stopwatch.Elapsed, timedOut);
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static async Task<string> ReadCappedAsync(StreamReader reader)
    {
        var sb = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            if (sb.Length < MaxOutputChars)
            {
                sb.Append(buffer, 0, Math.Min(read, MaxOutputChars - sb.Length));
            }
        }

        if (sb.Length >= MaxOutputChars)
        {
            sb.Append("\n… (출력이 잘렸습니다)");
        }

        return sb.ToString();
    }
}

/// <summary>Sample stdin JSON per hook event, shaped like Claude Code's hook input (editable before a run).</summary>
public static class HookSamples
{
    private static readonly JsonSerializerOptions s_write = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public static string For(string hookEvent, string matcher, string cwd)
    {
        var tool = ToolName(matcher);
        var input = new JsonObject
        {
            ["session_id"] = "dotdeck-test-session",
            ["transcript_path"] = Path.Combine(cwd, ".claude", "transcript.jsonl"),
            ["cwd"] = cwd,
            ["hook_event_name"] = hookEvent,
        };

        switch (hookEvent)
        {
            case "PreToolUse":
                input["tool_name"] = tool;
                input["tool_input"] = ToolInput(tool);
                break;
            case "PostToolUse":
                input["tool_name"] = tool;
                input["tool_input"] = ToolInput(tool);
                input["tool_response"] = new JsonObject { ["success"] = true };
                break;
            case "UserPromptSubmit":
                input["prompt"] = "테스트 프롬프트";
                break;
            case "Notification":
                input["message"] = "Claude needs your permission to use Bash";
                break;
            case "Stop" or "SubagentStop":
                input["stop_hook_active"] = false;
                break;
            case "PreCompact":
                input["trigger"] = "manual";
                input["custom_instructions"] = string.Empty;
                break;
            case "SessionStart":
                input["source"] = "startup";
                break;
            case "SessionEnd":
                input["reason"] = "exit";
                break;
        }

        return input.ToJsonString(s_write);
    }

    // A matcher like "Edit|Write" or "Bash" names tools; regex-like or empty matchers fall back to Bash.
    private static string ToolName(string matcher)
    {
        var first = matcher.Split('|')[0].Trim();
        return first.Length > 0 && first.All(char.IsLetterOrDigit) ? first : "Bash";
    }

    private static JsonObject ToolInput(string tool) => tool switch
    {
        "Bash" => new JsonObject { ["command"] = "git status", ["description"] = "Show working tree status" },
        "Read" => new JsonObject { ["file_path"] = "C:\\example\\README.md" },
        "Write" => new JsonObject { ["file_path"] = "C:\\example\\new.txt", ["content"] = "hello" },
        "Edit" or "MultiEdit" => new JsonObject { ["file_path"] = "C:\\example\\a.txt", ["old_string"] = "a", ["new_string"] = "b" },
        "WebFetch" => new JsonObject { ["url"] = "https://example.com", ["prompt"] = "summarize" },
        _ => [],
    };
}
