using System.Text;
using DotDeck.Core.AiCli.Scope;

namespace DotDeck.Core.AiCli.Guard;

/// <summary>
/// Process entry for <c>DotDeck.exe --dotdeck-guard &lt;scope.json&gt;</c>, registered as a PreToolUse hook. Reads the hook
/// input from stdin, the current scope from scope.json, and writes the decision JSON to stdout. Fails closed: any
/// error is a deny with exit code 2 (Claude Code blocks on exit 2 and shows stderr).
/// </summary>
public static class GuardHost
{
    public const string GuardSwitch = "--dotdeck-guard";
    public const string HeartbeatSwitch = "--dotdeck-heartbeat";
    public const string HeartbeatFileName = "heartbeat";

    /// <summary>Upper bound for the hook input; larger input is treated as malformed.</summary>
    private const int MaxInputBytes = 4 * 1024 * 1024;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <returns>The process exit code.</returns>
    public static int RunGuard(IReadOnlyList<string> args, Stream stdin, Stream stdout, Stream stderr, IFinalPathResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(args);
        try
        {
            if (args.Count != 2 || args[0] != GuardSwitch || !Path.IsPathFullyQualified(args[1]))
            {
                return Fail(stdout, stderr, "DotDeck 범위 가드: 실행 인자가 올바르지 않습니다");
            }

            var input = ReadInput(stdin);
            var scope = ScopeFile.Read(args[1]);
            var decision = new GuardEvaluator(resolver).EvaluateJson(input, scope.ToPolicy());
            var json = decision.ToHookOutputJson();
            if (json is not null)
            {
                Write(stdout, json);
            }

            return 0;
        }
        catch (Exception ex)
        {
            // Fail closed. The message never contains the hook input (it may hold file contents).
            return Fail(stdout, stderr, "DotDeck 범위 가드 오류로 차단했습니다: " + ex.GetType().Name + " " + Describe(ex));
        }
    }

    /// <summary>
    /// <c>DotDeck.exe --dotdeck-heartbeat &lt;sessionDir&gt;</c> (SessionStart hook): records that the session settings
    /// and therefore the guard were loaded. Always exits 0 so it never blocks the session itself.
    /// </summary>
    public static int RunHeartbeat(IReadOnlyList<string> args, Stream stdin, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(time);
        try
        {
            if (args.Count != 2 || args[0] != HeartbeatSwitch || !Path.IsPathFullyQualified(args[1]) || !Directory.Exists(args[1]))
            {
                return 0;
            }

            _ = ReadInput(stdin);
            File.WriteAllText(Path.Combine(args[1], HeartbeatFileName), time.GetUtcNow().ToString("O"), Utf8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
        }

        return 0;
    }

    private static string ReadInput(Stream stdin)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = stdin.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxInputBytes)
            {
                throw new InvalidDataException("훅 입력이 너무 큽니다");
            }
        }

        var bytes = buffer.ToArray();
        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, offset, bytes.Length - offset);
    }

    private static int Fail(Stream stdout, Stream stderr, string reason)
    {
        try
        {
            Write(stdout, GuardDecision.Deny(reason).ToHookOutputJson()!);
            Write(stderr, reason);
        }
        catch (IOException)
        {
        }

        return 2;
    }

    private static string Describe(Exception ex) => ex is ScopeFileException ? ex.Message : string.Empty;

    private static void Write(Stream stream, string text)
    {
        var bytes = Utf8.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }
}
