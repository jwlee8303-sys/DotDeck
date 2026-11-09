using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using DotDeck.Core.AiCli.Guard;
using DotDeck.Core.AiCli.Scope;

namespace DotDeck.Core.AiCli.Launch;

/// <summary>
/// Writes the per-session files a Claude Code launch points at: <c>settings.json</c> (passed with <c>--settings</c>;
/// merged with the user's own settings, never replacing them) and <c>scope.json</c>. The user's global settings are
/// never touched.
/// </summary>
public static class SessionFilesWriter
{
    /// <summary>
    /// Secret file name patterns denied to Claude's built-in read/edit tools (complements the guard, which cannot see
    /// what a folder-wide Grep reads). They cover every name <c>SecretFilePolicy</c> treats as secret. Permission rules
    /// have no exceptions, so dotenv templates (<c>.example</c> etc.) are denied here too even though the guard allows
    /// them: a deliberate over-block on the safe side.
    /// </summary>
    public static IReadOnlyList<string> SecretNamePatterns { get; } =
        [".env*", "*.pem", "*.key", "*.pfx", "id_rsa*", "id_ed25519*", "auth.json", ".credentials.json"];

    /// <summary>
    /// Permission rule paths for the secret patterns: <c>**/</c> (relative to the working directory) plus one absolute
    /// <c>//c/…/**/</c> form per scope root, because relative rules do not reach additional directories. Roots that
    /// cannot be written as a rule path (UNC, glob or rule syntax characters) rely on the guard alone.
    /// </summary>
    public static IReadOnlyList<string> SecretRulePaths(IEnumerable<string> roots)
    {
        var bases = new List<string> { "**/" };
        foreach (var root in roots)
        {
            if (ToRulePath(root) is { } rule && !bases.Contains(rule, StringComparer.OrdinalIgnoreCase))
            {
                bases.Add(rule);
            }
        }

        return [.. bases.SelectMany(b => SecretNamePatterns.Select(p => b + p))];
    }

    /// <summary><c>C:\work\a b</c> → <c>//c/work/a b/**/</c> (Claude Code's absolute rule form); null when not representable.</summary>
    internal static string? ToRulePath(string root)
    {
        if (root.Length < 3 || !char.IsAsciiLetter(root[0]) || root[1] != ':' || root[2] != '\\'
            || root.IndexOfAny(['*', '?', '[', ']', '{', '}', '(', ')', '"']) >= 0)
        {
            return null;
        }

        var rest = root[3..].Replace('\\', '/').TrimEnd('/');
        return "//" + char.ToLowerInvariant(root[0]) + "/" + (rest.Length == 0 ? string.Empty : rest + "/") + "**/";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <summary>Builds the session settings JSON.</summary>
    /// <param name="guardExecutable">Absolute path of DotDeck.exe (<c>Environment.ProcessPath</c>).</param>
    public static string BuildSettings(string guardExecutable, string sessionDirectory, ScopeDocument scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!Path.IsPathFullyQualified(guardExecutable) || !Path.IsPathFullyQualified(sessionDirectory))
        {
            throw new ArgumentException("가드 실행 파일과 세션 폴더는 절대 경로여야 합니다");
        }

        var deny = new JsonArray();
        foreach (var pattern in SecretRulePaths(scope.Roots.Select(r => r.Path).Concat(scope.ReadOnlyRoots)))
        {
            deny.Add($"Read({pattern})");
            deny.Add($"Edit({pattern})");
        }

        var permissions = new JsonObject
        {
            ["blockWritesOutsideWorkingDirectories"] = true,
            ["deny"] = deny,
        };
        if (!scope.AllowOutsideReads)
        {
            permissions["blockReadsOutsideWorkingDirectories"] = true;
        }

        var settings = new JsonObject
        {
            ["permissions"] = permissions,
            ["hooks"] = new JsonObject
            {
                ["PreToolUse"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["matcher"] = "*",
                        ["hooks"] = new JsonArray
                        {
                            ExecHook(guardExecutable, [GuardHost.GuardSwitch, Path.Combine(sessionDirectory, SessionFileNames.Scope)], timeout: 10, blockOnFailure: true),
                        },
                    },
                },
                ["SessionStart"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["hooks"] = new JsonArray
                        {
                            ExecHook(guardExecutable, [GuardHost.HeartbeatSwitch, sessionDirectory], timeout: 10, blockOnFailure: false),
                        },
                    },
                },
            },
        };
        return settings.ToJsonString(JsonOptions);
    }

    /// <summary>Writes settings.json and scope.json into the session folder.</summary>
    public static void Write(string guardExecutable, string sessionDirectory, ScopeDocument scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Directory.CreateDirectory(sessionDirectory);
        ScopeFile.Write(Path.Combine(sessionDirectory, SessionFileNames.Scope), scope);
        var settingsPath = Path.Combine(sessionDirectory, SessionFileNames.Settings);
        var temp = settingsPath + ".tmp";
        File.WriteAllText(temp, BuildSettings(guardExecutable, sessionDirectory, scope), new UTF8Encoding(false));
        File.Move(temp, settingsPath, overwrite: true);
    }

    /// <summary>Exec-form hook (command + args, no shell; verified with Claude Code 2.1.319).</summary>
    private static JsonObject ExecHook(string executable, string[] args, int timeout, bool blockOnFailure)
    {
        var hook = new JsonObject
        {
            ["type"] = "command",
            ["command"] = executable,
            ["args"] = new JsonArray([.. args.Select(a => (JsonNode)a)]),
            ["timeout"] = timeout,
        };
        if (blockOnFailure)
        {
            hook["onFailure"] = "block";
        }

        return hook;
    }
}
