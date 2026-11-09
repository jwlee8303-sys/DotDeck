using System.Text;
using DotDeck.Core.AiCli.Context;
using DotDeck.Core.AiCli.Launch;
using DotDeck.Core.AiCli.Presets;
using DotDeck.Core.AiCli.Scope;
using DotDeck.Core.Files;
using DotDeck.Core.Paths;
using DotDeck.Core.Projects;

namespace DotDeck.Core.AiCli.Sessions;

public enum AiSessionState
{
    Running,
    Exited,
    Failed,
    Stopped,
}

/// <summary>What the user chose to start (dialog, quick launch or preset).</summary>
public sealed record AiSessionStartRequest
{
    public required string PrimaryProjectId { get; init; }

    public IReadOnlyList<string> AdditionalProjectIds { get; init; } = [];

    public CliKind Cli { get; init; } = CliKind.Claude;

    public LaunchMode Mode { get; init; } = LaunchMode.NonInteractive;

    public string? Prompt { get; init; }

    public bool IncludeOpenDocuments { get; init; } = true;

    public IReadOnlyList<string> OpenDocuments { get; init; } = [];

    public string? ActiveDocument { get; init; }

    public IReadOnlyList<string>? GlobalExtensions { get; init; }

    public bool ResumeConversation { get; init; }

    public bool AllowReadOnlyShell { get; init; }

    public decimal? MaxBudgetUsd { get; init; }
}

/// <summary>A start that cannot proceed; the message is shown to the user as is.</summary>
public sealed class AiSessionStartException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Everything the manager needs from the app (all replaceable in tests).</summary>
public sealed record AiSessionEnvironment
{
    public required AppPaths Paths { get; init; }

    public required IFileStore Store { get; init; }

    public required Func<IReadOnlyList<ProjectDefinition>> Projects { get; init; }

    public required ICliProcessLauncher Launcher { get; init; }

    public required Func<CliKind, CliLocatorResult> Locate { get; init; }

    /// <summary>DotDeck.exe (<c>Environment.ProcessPath</c>), registered as the guard and heartbeat hook.</summary>
    public required string GuardExecutable { get; init; }

    public SessionPresetStore? Presets { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Claude must run the SessionStart heartbeat hook within this time, or the session settings did not apply.</summary>
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>One running or finished CLI session.</summary>
public sealed class AiSession
{
    private readonly Lock _gate = new();

    internal AiSession(string id, SessionScope scope, AiSessionStartRequest request, string directory, DateTimeOffset startedAt)
    {
        Id = id;
        Scope = scope;
        Request = request;
        Directory = directory;
        StartedAt = startedAt;
    }

    public string Id { get; }

    public AiSessionStartRequest Request { get; }

    public CliKind Cli => Request.Cli;

    public LaunchMode Mode => Request.Mode;

    public string Directory { get; }

    public DateTimeOffset StartedAt { get; }

    /// <summary>The current scope; narrows when projects leave it while the session runs.</summary>
    public SessionScope Scope { get; internal set; }

    public string Title => Scope.Title(Cli == CliKind.Claude ? "Claude" : "Codex");

    public string? ConversationId { get; internal set; }

    public string CommandLinePreview { get; internal set; } = string.Empty;

    public AiSessionState State { get; private set; } = AiSessionState.Running;

    public int? ExitCode { get; private set; }

    /// <summary>Why the session failed or was stopped (user-facing).</summary>
    public string? Message { get; private set; }

    internal ICliProcess? Process { get; set; }

    internal long ScopeGeneration { get; set; }

    /// <summary>A line of stdout (<c>isError</c> false) or stderr. Raised on a background thread.</summary>
    public event Action<AiSession, string, bool>? OutputLine;

    /// <summary>State changed. Raised on a background thread.</summary>
    public event Action<AiSession>? StateChanged;

    internal void RaiseOutput(string line, bool isError) => OutputLine?.Invoke(this, line, isError);

    /// <summary>Moves out of Running once; later calls are ignored (the first reason wins).</summary>
    internal bool Finish(AiSessionState state, int? exitCode, string? message)
    {
        lock (_gate)
        {
            if (State != AiSessionState.Running)
            {
                if (exitCode is not null && ExitCode is null)
                {
                    ExitCode = exitCode;
                }

                return false;
            }

            State = state;
            ExitCode = exitCode;
            Message = message;
        }

        StateChanged?.Invoke(this);
        return true;
    }
}

/// <summary>
/// Starts and tracks AI CLI sessions (SPEC 11.5, DESIGN 5a-7): prepares the session folder (scope.json, settings.json,
/// context.md), builds and checks the command line, launches the CLI in a Job, watches the heartbeat, streams output
/// and keeps scope.json in step with the project list.
/// </summary>
public sealed class AiSessionManager : IDisposable
{
    /// <summary>CreateProcess accepts 32,767 characters including the terminator; keep a margin.</summary>
    public const int MaxCommandLineLength = 32_000;

    private static readonly string[] ClaudeReadOnlyFolders = ["skills", "agents", "commands", "plugins", "rules"];

    private readonly AiSessionEnvironment _env;
    private readonly Lock _gate = new();
    private readonly List<AiSession> _sessions = [];

    public AiSessionManager(AiSessionEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _env = environment;
    }

    /// <summary>Session list or a session state changed. Raised on any thread.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<AiSession> Sessions
    {
        get
        {
            lock (_gate)
            {
                return [.. _sessions];
            }
        }
    }

    public int RunningCount
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count(s => s.State == AiSessionState.Running);
            }
        }
    }

    /// <exception cref="AiSessionStartException">The session cannot start (message for the user).</exception>
    public AiSession Start(AiSessionStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Cli == CliKind.Codex)
        {
            throw new AiSessionStartException("Codex 세션은 이후 단계(5c)에서 지원합니다.");
        }

        if (request.Mode == LaunchMode.Interactive)
        {
            throw new AiSessionStartException("대화형 터미널은 이후 단계(5b)에서 지원합니다.");
        }

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            throw new AiSessionStartException("실행할 프롬프트를 입력하세요.");
        }

        var projects = _env.Projects();
        var created = SessionScope.Create(request.PrimaryProjectId, request.AdditionalProjectIds, projects, _env.Paths, _env.Store);
        if (created.Scope is not { } scope)
        {
            throw new AiSessionStartException(created.Error ?? "세션 범위를 만들 수 없습니다.");
        }

        var located = _env.Locate(request.Cli);
        if (located.Location is not { } cli)
        {
            throw new AiSessionStartException(located.Error ?? "CLI를 찾을 수 없습니다.");
        }

        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(_env.Paths.AiSessionsDir, id);
        var session = new AiSession(id, scope, request, directory, _env.Time.GetUtcNow());

        var conversation = PickConversation(request, scope);
        session.ConversationId = conversation.Id;
        CliLaunchPlan plan;
        try
        {
            var context = ContextDocumentBuilder.Build(new ContextInput
            {
                Scope = scope,
                Projects = projects,
                GlobalExtensions = request.GlobalExtensions,
                IncludeOpenDocuments = request.IncludeOpenDocuments,
                OpenDocuments = request.OpenDocuments,
                ActiveDocument = request.ActiveDocument,
            });

            plan = CliCommandBuilder.Build(new CliLaunchRequest
            {
                Cli = cli,
                Mode = request.Mode,
                Scope = scope,
                SessionDirectory = directory,
                Context = context,
                ConversationId = conversation.Id,
                Resume = conversation.Resume,
                Prompt = request.Prompt,
                AllowReadOnlyShell = request.AllowReadOnlyShell,
                MaxBudgetUsd = request.MaxBudgetUsd,
                SandboxClaudeConfigDir = _env.Paths.IsHomeOverridden ? _env.Paths.ClaudeHome : null,
                SandboxCodexHome = _env.Paths.IsHomeOverridden ? _env.Paths.CodexHome : null,
            });
            ForbiddenOptions.Validate(plan);
            if (plan.CommandLine.Length > MaxCommandLineLength)
            {
                // Escaping (TOML, command-line quoting) can grow the context several times over.
                throw new AiSessionStartException($"명령줄이 너무 깁니다({plan.CommandLine.Length:N0}자). 열린 문서 전달을 끄거나 추가 프로젝트를 줄이세요.");
            }

            SessionFilesWriter.Write(_env.GuardExecutable, directory, ScopeDocumentFor(session, scope, generation: 1));
            File.WriteAllText(Path.Combine(directory, SessionFileNames.Context), context, new UTF8Encoding(false));
            session.ScopeGeneration = 1;
            session.CommandLinePreview = plan.CommandLine;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw new AiSessionStartException($"세션을 준비하지 못했습니다: {ex.Message}", ex);
        }

        try
        {
            session.Process = _env.Launcher.Start(plan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new AiSessionStartException($"CLI를 실행하지 못했습니다: {ex.Message}", ex);
        }

        RememberConversation(scope, request.Cli, conversation.Id);
        session.StateChanged += _ => Changed?.Invoke(this, EventArgs.Empty);
        lock (_gate)
        {
            _sessions.Add(session);
        }

        var stdout = PumpAsync(session, session.Process.StandardOutput, isError: false);
        var stderr = PumpAsync(session, session.Process.StandardError, isError: true);
        _ = WatchExitAsync(session, stdout, stderr);
        _ = WatchHeartbeatAsync(session);
        Changed?.Invoke(this, EventArgs.Empty);
        return session;
    }

    /// <summary>Stops a running session (terminates its whole process tree).</summary>
    public void Stop(AiSession session, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Finish(AiSessionState.Stopped, null, reason ?? "사용자가 중지했습니다."))
        {
            session.Process?.Terminate();
        }
    }

    /// <summary>
    /// Re-checks running sessions against the project list (call on <c>ProjectRegistry.Changed</c>). A project that
    /// was removed, became unselectable or moved to another root leaves the scope at once (scope.json is rewritten,
    /// the guard reads it on the next tool call). Losing the primary project stops the session.
    /// </summary>
    public void OnProjectsChanged()
    {
        var projects = _env.Projects();
        foreach (var session in Sessions.Where(s => s.State == AiSessionState.Running))
        {
            var current = session.Scope;
            var fresh = SessionScope.Create(current.Primary.ProjectId, current.Additional.Select(p => p.ProjectId), projects, _env.Paths, _env.Store).Scope;
            if (fresh is null || !SameRoot(fresh.Primary, current.Primary))
            {
                Stop(session, "주 프로젝트가 제거되었거나 위치가 바뀌어 세션을 중지했습니다.");
                continue;
            }

            var kept = current.Additional.Where(old => fresh.Additional.Any(n => n.ProjectId == old.ProjectId && SameRoot(n, old))).ToList();
            if (kept.Count == current.Additional.Count)
            {
                continue;
            }

            var narrowed = current;
            foreach (var removed in current.Additional.Where(p => !kept.Contains(p)))
            {
                narrowed = narrowed.Without(removed.ProjectId)!; // only additional projects are removed
            }

            try
            {
                var generation = session.ScopeGeneration + 1;
                ScopeFile.Write(Path.Combine(session.Directory, SessionFileNames.Scope), ScopeDocumentFor(session, narrowed, generation));
                session.ScopeGeneration = generation;
                session.Scope = narrowed;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Never leave a session running with a scope wider than the user's projects.
                Stop(session, $"범위 파일을 갱신하지 못해 세션을 중지했습니다: {ex.Message}");
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        foreach (var session in Sessions)
        {
            Stop(session, "DotDeck이 종료되어 세션을 중지했습니다.");
            session.Process?.Dispose();
        }
    }

    private static bool SameRoot(ScopeProject a, ScopeProject b) => string.Equals(a.Root, b.Root, StringComparison.OrdinalIgnoreCase);

    private ScopeDocument ScopeDocumentFor(AiSession session, SessionScope scope, long generation) => new()
    {
        SessionId = session.Id,
        Generation = generation,
        UpdatedAt = _env.Time.GetUtcNow(),
        Roots = [.. scope.Projects
            .GroupBy(p => p.Root, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ScopeFileRoot { ProjectId = g.First().ProjectId, Name = g.First().Name, Path = g.Key })],
        ReadOnlyRoots = [.. ClaudeReadOnlyFolders.Select(f => Path.Combine(_env.Paths.ClaudeHome, f))],
        Home = _env.Paths.Home,
    };

    private (string? Id, bool Resume) PickConversation(AiSessionStartRequest request, SessionScope scope)
    {
        if (request.Cli != CliKind.Claude)
        {
            return (null, false);
        }

        if (request.ResumeConversation && _env.Presets?.GetConversation(scope.Key)?.ClaudeSessionId is { } previous && Guid.TryParse(previous, out _))
        {
            return (previous, true);
        }

        return (Guid.NewGuid().ToString(), false);
    }

    private void RememberConversation(SessionScope scope, CliKind cli, string? id)
    {
        if (id is null || _env.Presets is not { Status: not PresetStoreLoadStatus.ReadOnly } presets)
        {
            return;
        }

        try
        {
            presets.SetConversation(scope.Key, cli, id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Remembering is a convenience; the session itself is already running.
        }
    }

    private static async Task PumpAsync(AiSession session, Stream stream, bool isError)
    {
        try
        {
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                session.RaiseOutput(line, isError);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The pipe closes when the process tree ends.
        }
    }

    /// <summary>
    /// The single place that decides how a session ended. When the main process exits, whatever it left running in the
    /// job is ended too (no CLI child outlives its session unguarded), and the process handles are released once the
    /// output has been read. A Claude run that ends without the SessionStart heartbeat never loaded the session
    /// settings (SessionStart also runs for -p, measured in 5a-4), so it is a failure even with exit code 0.
    /// </summary>
    private async Task WatchExitAsync(AiSession session, Task stdout, Task stderr)
    {
        var process = session.Process!;
        var code = await process.Exited.ConfigureAwait(false);
        process.Terminate(code);
        await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        if (session.Cli == CliKind.Claude && !File.Exists(Path.Combine(session.Directory, Guard.GuardHost.HeartbeatFileName)))
        {
            session.Finish(AiSessionState.Failed, code, HeartbeatMissing);
        }
        else
        {
            session.Finish(code == 0 ? AiSessionState.Exited : AiSessionState.Failed, code, code == 0 ? null : $"CLI가 종료 코드 {code}로 끝났습니다.");
        }

        process.Dispose();
    }

    private const string HeartbeatMissing = "세션 설정(가드 훅)이 적용되지 않아 세션을 중지했습니다. CLI 버전과 설정을 확인하세요.";

    private async Task WatchHeartbeatAsync(AiSession session)
    {
        if (session.Cli != CliKind.Claude)
        {
            return;
        }

        var heartbeat = Path.Combine(session.Directory, Guard.GuardHost.HeartbeatFileName);
        var exited = session.Process!.Exited;
        var timeout = Task.Delay(_env.HeartbeatTimeout, _env.Time);
        await Task.WhenAny(exited, timeout).ConfigureAwait(false);
        if (exited.IsCompleted || File.Exists(heartbeat))
        {
            return;
        }

        if (session.Finish(AiSessionState.Failed, null, HeartbeatMissing))
        {
            session.Process.Terminate();
        }
    }
}
