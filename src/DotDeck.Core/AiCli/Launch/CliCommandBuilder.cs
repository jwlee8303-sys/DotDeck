using System.Globalization;

namespace DotDeck.Core.AiCli.Launch;

/// <summary>Options DotDeck never passes (SPEC 11.5 "권한 우회 옵션은 사용하지 않는다", DESIGN ForbiddenOptions).</summary>
public static class ForbiddenOptions
{
    public static IReadOnlyList<string> All { get; } =
    [
        "--dangerously-skip-permissions",
        "--allow-dangerously-skip-permissions",
        "bypassPermissions",
        "--dangerously-bypass-approvals-and-sandbox",
        "--dangerously-bypass-hook-trust",
        "danger-full-access",
        "--approve-for-me",
        "--bg",
        "--background",
        "--remote-control",
        "--yolo",
    ];

    /// <summary>Throws when any non-free-text argument is (or sets) a forbidden option.</summary>
    public static void Validate(CliLaunchPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        for (var i = 0; i < plan.Arguments.Count; i++)
        {
            if (plan.FreeTextArguments.Contains(i))
            {
                continue;
            }

            var argument = plan.Arguments[i];
            foreach (var forbidden in All)
            {
                if (argument.Equals(forbidden, StringComparison.OrdinalIgnoreCase)
                    || argument.StartsWith(forbidden + "=", StringComparison.OrdinalIgnoreCase)
                    || argument.Contains("=" + forbidden, StringComparison.OrdinalIgnoreCase)
                    || argument.Contains("\"" + forbidden + "\"", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"금지된 CLI 옵션: {forbidden}");
                }
            }
        }
    }
}

/// <summary>Session files that the command line points at (written by <see cref="SessionFilesWriter"/>).</summary>
public static class SessionFileNames
{
    public const string Settings = "settings.json";
    public const string Scope = "scope.json";
    public const string Context = "context.md";
    public const string LastMessage = "last-message.txt";
}

/// <summary>Builds Claude Code and Codex command lines from a launch request (DESIGN "명령줄").</summary>
public static class CliCommandBuilder
{
    /// <summary>Built-in file tools for non-interactive Claude runs; shell tools are excluded by default (A14).</summary>
    public const string NonInteractiveTools = "Read,Glob,Grep,Edit,Write,NotebookEdit";

    public const string ReadOnlyShellAllowedTools = "Bash(git status),Bash(git diff *),Bash(git log *)";

    public static CliLaunchPlan Build(CliLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var plan = request.Cli.Cli switch
        {
            CliKind.Claude => BuildClaude(request),
            CliKind.Codex => BuildCodex(request),
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
        ForbiddenOptions.Validate(plan);
        return plan;
    }

    private sealed class Args
    {
        public List<string> Items { get; } = [];

        public HashSet<int> FreeText { get; } = [];

        public void Add(params string[] values) => Items.AddRange(values);

        public void AddFreeText(string option, string text)
        {
            Items.Add(option);
            FreeText.Add(Items.Count);
            Items.Add(text);
        }
    }

    private static CliLaunchPlan BuildClaude(CliLaunchRequest request)
    {
        var args = new Args();
        args.Add([.. request.Cli.PrefixArguments]);
        string? stdin = null;
        if (request.Mode == LaunchMode.NonInteractive)
        {
            if (string.IsNullOrWhiteSpace(request.Prompt))
            {
                throw new ArgumentException("명령 실행에는 프롬프트가 필요합니다", nameof(request));
            }

            // The prompt goes through stdin (verified with 2.1.319): no command-line length limit and no risk of a
            // leading '-' being parsed as an option. stream-json in print mode requires --verbose (verified).
            stdin = request.Prompt;
            args.Add("-p");
            args.Add("--output-format", "stream-json", "--verbose");
            args.Add("--permission-mode", "dontAsk", "--permission-prompts", "none");
            var tools = request.AllowReadOnlyShell ? NonInteractiveTools + ",Bash" : NonInteractiveTools;
            args.Add("--tools", tools);
            var allowed = request.AllowReadOnlyShell ? NonInteractiveTools + "," + ReadOnlyShellAllowedTools : NonInteractiveTools;
            if (request.McpConfigPath is not null)
            {
                allowed += ",mcp__dotdeck_session__*";
            }

            args.Add("--allowedTools", allowed);
            args.Add("--strict-mcp-config");
            if (request.MaxBudgetUsd is { } budget)
            {
                args.Add("--max-budget-usd", budget.ToString(CultureInfo.InvariantCulture));
            }
        }

        foreach (var dir in request.Scope.AdditionalDirectories)
        {
            args.Add("--add-dir", dir);
        }

        args.Add("--settings", Path.Combine(request.SessionDirectory, SessionFileNames.Settings));
        if (request.McpConfigPath is not null)
        {
            args.Add("--mcp-config", request.McpConfigPath);
        }

        if (request.ConversationId is { } id)
        {
            if (!Guid.TryParse(id, out _))
            {
                throw new ArgumentException("대화 ID는 UUID여야 합니다", nameof(request));
            }

            if (request.Resume)
            {
                args.Add("--resume", id, "--system-prompt-snapshot", "off");
            }
            else
            {
                args.Add("--session-id", id);
            }
        }

        if (request.Mode == LaunchMode.Interactive)
        {
            args.Add("-n", request.Scope.Title("Claude"));
        }

        if (!string.IsNullOrEmpty(request.Context))
        {
            args.AddFreeText("--append-system-prompt", request.Context);
        }

        return new CliLaunchPlan
        {
            Executable = request.Cli.Executable,
            Arguments = args.Items,
            WorkingDirectory = request.Scope.Primary.Root,
            Environment = SandboxEnvironment(request),
            StandardInput = stdin,
            FreeTextArguments = args.FreeText,
        };
    }

    private static CliLaunchPlan BuildCodex(CliLaunchRequest request)
    {
        if (request.Resume && (request.Mode != LaunchMode.NonInteractive || !Guid.TryParse(request.ConversationId, out _)))
        {
            throw new NotSupportedException("Codex 이어가기는 비대화형에서 유효한 대화 ID로만 지원합니다");
        }

        var args = new Args();
        args.Add([.. request.Cli.PrefixArguments]);
        string? stdin = null;
        if (request.Mode == LaunchMode.NonInteractive)
        {
            if (string.IsNullOrWhiteSpace(request.Prompt))
            {
                throw new ArgumentException("명령 실행에는 프롬프트가 필요합니다", nameof(request));
            }

            args.Add("exec");
            args.Add("--skip-git-repo-check");
            stdin = request.Prompt;
        }

        args.Add("-C", request.Scope.Primary.Root);
        foreach (var dir in request.Scope.AdditionalDirectories)
        {
            args.Add("--add-dir", dir);
        }

        args.Add("-s", "workspace-write");
        // Replace inherited grants with exactly the selected roots; keep --add-dir for CLI context as well.
        args.Add("-c", "sandbox_workspace_write.writable_roots=[" + string.Join(",", request.Scope.Roots.Select(TomlString.Basic)) + "]");
        if (request.WindowsSandboxMode is "elevated" or "unelevated" or "mxc")
            args.Add("-c", "windows.sandbox=" + TomlString.Basic(request.WindowsSandboxMode));
        if (request.Mode == LaunchMode.NonInteractive)
        {
            // `codex exec` has no -a; the approval policy is set through config.
            args.Add("-c", "approval_policy=" + TomlString.Basic("never"));
        }
        else
        {
            args.Add("-a", "on-request", "--no-daemon");
        }

        if (!string.IsNullOrEmpty(request.Context))
        {
            args.FreeText.Add(args.Items.Count + 1);
            args.Add("-c", "developer_instructions=" + TomlString.Basic(request.Context));
        }

        if (request.McpBridgeExecutable is { } bridge)
        {
            var sessionId = Path.GetFileName(request.SessionDirectory);
            var tools = string.Join(",", Mcp.McpServer.Names.Select(name => TomlString.Basic(name) + "={approval_mode=\"approve\"}"));
            var config = "mcp_servers.dotdeck_session={command=" + TomlString.Basic(bridge)
                + ",args=[\"--dotdeck-mcp\"," + TomlString.Basic(sessionId)
                + "],env={DOTDECK_MCP_DIRECTORY=" + TomlString.Basic(request.SessionDirectory)
                + "},required=true,default_tools_approval_mode=\"prompt\",enabled_tools=["
                + string.Join(",", Mcp.McpServer.Names.Select(TomlString.Basic)) + "],tools={" + tools + "}}";
            args.Add("-c", config);
        }

        if (request.Mode == LaunchMode.NonInteractive)
        {
            if (request.Resume) args.Add("resume", request.ConversationId!);
            args.Add("--json", "-o", Path.Combine(request.SessionDirectory, SessionFileNames.LastMessage), "-");
        }

        return new CliLaunchPlan
        {
            Executable = request.Cli.Executable,
            Arguments = args.Items,
            WorkingDirectory = request.Scope.Primary.Root,
            Environment = SandboxEnvironment(request),
            StandardInput = stdin,
            FreeTextArguments = args.FreeText,
        };
    }

    /// <summary>A4: only sandbox runs redirect the CLI homes; real-home runs pass nothing.</summary>
    private static Dictionary<string, string> SandboxEnvironment(CliLaunchRequest request)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (request.SandboxClaudeConfigDir is { } claude)
        {
            env["CLAUDE_CONFIG_DIR"] = claude;
        }

        if (request.SandboxCodexHome is { } codex)
        {
            env["CODEX_HOME"] = codex;
        }

        return env;
    }
}
