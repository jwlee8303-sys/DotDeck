using System.Text;
using DotDeck.Core.AiCli.Context;
using DotDeck.Core.AiCli.Launch;
using DotDeck.Core.AiCli.Presets;
using DotDeck.Core.AiCli.Scope;
using DotDeck.Core.AiCli.Snapshots;
using DotDeck.Core.Documents;
using DotDeck.Core.Explorer;
using DotDeck.Core.FileOperations;
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

    /// <summary>
    /// The exact Claude conversation to resume ("프로젝트 추가/다시 시작" keeps the conversation even though the scope,
    /// and so the remembered conversation of that scope, changes). Used only with <see cref="ResumeConversation"/>.
    /// </summary>
    public string? ResumeConversationId { get; init; }

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

    /// <summary>Starts interactive sessions in a pseudo console; without one, interactive starts are refused.</summary>
    public ITerminalLauncher? TerminalLauncher { get; init; }

    public required Func<CliKind, CliLocatorResult> Locate { get; init; }

    /// <summary>DotDeck.exe (<c>Environment.ProcessPath</c>), registered as the guard and heartbeat hook.</summary>
    public required string GuardExecutable { get; init; }

    public SessionPresetStore? Presets { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Claude must run the SessionStart heartbeat hook within this time, or the session settings did not apply.</summary>
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Encrypts captured contents like every DotDeck backup (DPAPI in the app).</summary>
    public IBackupProtector? BackupProtector { get; init; }

    public SnapshotLimits SnapshotLimits { get; init; } = new();

    public Func<string, bool> BackupExcluded { get; init; } = _ => false;

    public Func<string, bool> ReviewExcluded { get; init; } = _ => false;

    public Func<string, bool> ReviewDirectoryExcluded { get; init; } = _ => false;

    /// <summary>Where reverted "created" files go. Without one, such reverts are refused (never a permanent delete).</summary>
    public IRecycleBin? RecycleBin { get; init; }

    public Func<AiSession, IDisposable>? StartMcpHost { get; init; }

    public Func<string?> CodexSandboxMode { get; init; } = () => null;

    public IFinalPathResolver? FinalPathResolver { get; init; }
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

    internal string? CliFailure { get; set; }

    internal ICliProcess? Process { get; set; }

    internal IDisposable? McpHost { get; set; }

    internal IDisposable? DirectoryLease { get; set; }

    /// <summary>The console of an interactive session (null for command runs). Kept after the exit for its replay.</summary>
    public TerminalConnection? Terminal { get; internal set; }

    internal Task<int> ProcessExited => Terminal?.Exited ?? Process?.Exited ?? Task.FromResult(-1);

    /// <summary>
    /// Completes when the CLI process has actually ended (a stopped session's process may still be shutting down).
    /// A restart waits for it before resuming the same conversation.
    /// </summary>
    public Task WhenProcessEnded => ProcessExited;

    internal void TerminateProcess()
    {
        if (Terminal is { } terminal)
        {
            terminal.Terminate();
        }
        else
        {
            Process?.Terminate();
        }
    }

    internal void DisposeProcess()
    {
        Terminal?.Dispose();
        Process?.Dispose();
        DirectoryLease?.Dispose();
    }

    /// <summary>The scope index and captured contents taken just before the CLI started.</summary>
    public SessionSnapshot? Snapshot { get; internal set; }

    /// <summary>Roots indexed at start (kept even if the scope narrows later, so their changes are still reported).</summary>
    internal IReadOnlyList<SnapshotRoot> SnapshotRoots { get; set; } = [];

    internal long ScopeGeneration { get; set; }

    /// <summary>A line of stdout (<c>isError</c> false) or stderr. Raised on a background thread.</summary>
    public event Action<AiSession, string, bool>? OutputLine;

    /// <summary>State changed. Raised on a background thread.</summary>
    public event Action<AiSession>? StateChanged;

    /// <summary>Recent output lines kept for viewers that attach late (the panel opens after the CLI started).</summary>
    public const int OutputBufferLines = 5000;

    /// <summary>Total characters kept (stream JSON lines can carry whole tool results).</summary>
    public const long OutputBufferChars = 4L * 1024 * 1024;

    private readonly Queue<(string Line, bool IsError)> _recent = new();
    private long _recentChars;
    private Action<AiSession, string, bool>[] _viewers = [];

    /// <summary>True once Claude ran the SessionStart heartbeat hook, i.e. the session settings and the guard apply.</summary>
    public bool GuardConnected { get; private set; }

    internal void MarkGuardConnected()
    {
        lock (_gate)
        {
            if (GuardConnected)
            {
                return;
            }

            GuardConnected = true;
        }

        StateChanged?.Invoke(this);
    }

    /// <summary>
    /// Returns the buffered lines and subscribes <paramref name="viewer"/> to later ones, atomically: every line is
    /// seen exactly once, either in the returned list or through the callback (raised on a background thread).
    /// </summary>
    public IReadOnlyList<(string Line, bool IsError)> AttachOutput(Action<AiSession, string, bool> viewer)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        lock (_gate)
        {
            _viewers = [.. _viewers, viewer];
            return [.. _recent];
        }
    }

    public void DetachOutput(Action<AiSession, string, bool> viewer)
    {
        lock (_gate)
        {
            _viewers = [.. _viewers.Where(v => v != viewer)];
        }
    }

    internal void RaiseOutput(string line, bool isError)
    {
        Action<AiSession, string, bool>[] viewers;
        lock (_gate)
        {
            // A single huge line (a whole tool result) is passed to live viewers but never kept: the buffer
            // stays within its size limit no matter what the CLI prints.
            if (line.Length <= OutputBufferChars / 4)
            {
                _recent.Enqueue((line, isError));
                _recentChars += line.Length;
                while (_recent.Count > OutputBufferLines || _recentChars > OutputBufferChars)
                {
                    _recentChars -= _recent.Dequeue().Line.Length;
                }
            }

            viewers = _viewers;
        }

        OutputLine?.Invoke(this, line, isError);
        foreach (var viewer in viewers)
        {
            viewer(this, line, isError);
        }
    }

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

        McpHost?.Dispose();
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

    /// <summary>
    /// The command line a start would run ("명령줄 미리보기", read-only), with a placeholder session folder. Nothing is
    /// written. Returns the reason instead when the request cannot start.
    /// </summary>
    public string PreviewCommandLine(AiSessionStartRequest request) => PreviewCore(request);

    /// <summary>Presets, last additional projects and conversation ids (null when sessions.json is not used).</summary>
    public SessionPresetStore? Presets => _env.Presets;

    private string? _codexSandboxMode;

    public bool IsCodexSandboxConfigured
    {
        get
        {
            var mode = _env.CodexSandboxMode();
            Volatile.Write(ref _codexSandboxMode, mode);
            return mode is "elevated" or "unelevated" or "mxc";
        }
    }

    private string PreviewCore(AiSessionStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var projects = _env.Projects();
        var created = SessionScope.Create(request.PrimaryProjectId, request.AdditionalProjectIds, projects, _env.Paths, _env.Store);
        if (created.Scope is not { } scope)
        {
            return created.Error ?? "세션 범위를 만들 수 없습니다.";
        }

        if (_env.Locate(request.Cli).Location is not { } cli)
        {
            return _env.Locate(request.Cli).Error ?? "CLI를 찾을 수 없습니다.";
        }

        try
        {
            var conversation = PickConversation(request, scope);
            var plan = CliCommandBuilder.Build(new CliLaunchRequest
            {
                Cli = cli,
                Mode = request.Mode,
                Scope = scope,
                SessionDirectory = Path.Combine(_env.Paths.AiSessionsDir, "(새 세션)"),
                Context = "(컨텍스트 문서)",
                ConversationId = conversation.Id,
                Resume = conversation.Resume,
                Prompt = request.Prompt,
                AllowReadOnlyShell = request.AllowReadOnlyShell,
                MaxBudgetUsd = request.MaxBudgetUsd,
                McpConfigPath = _env.StartMcpHost is null ? null : Path.Combine(_env.Paths.AiSessionsDir, "(새 세션)", "mcp.json"),
                McpBridgeExecutable = _env.StartMcpHost is null ? null : _env.GuardExecutable,
                WindowsSandboxMode = request.Cli == CliKind.Codex ? Volatile.Read(ref _codexSandboxMode) : null,
                SandboxClaudeConfigDir = _env.Paths.IsHomeOverridden ? _env.Paths.ClaudeHome : null,
                SandboxCodexHome = _env.Paths.IsHomeOverridden ? _env.Paths.CodexHome : null,
            });
            // The prompt never appears on the command line (it is not limited by the 32K command-line length).
            return plan.StandardInput is null ? plan.CommandLine : plan.CommandLine + Environment.NewLine + Environment.NewLine + "(프롬프트는 표준 입력으로 전달됩니다)";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException or AiSessionStartException)
        {
            return ex.Message;
        }
    }

    /// <exception cref="AiSessionStartException">The session cannot start (message for the user).</exception>
    public AiSession Start(AiSessionStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Pin exactly the mode that passed validation, even if config.toml changes during preparation.
        var windowsSandboxMode = request.Cli == CliKind.Codex ? _env.CodexSandboxMode() : null;
        if (request.Cli == CliKind.Codex && request.Mode == LaunchMode.NonInteractive && windowsSandboxMode is not ("elevated" or "unelevated" or "mxc"))
        {
            throw new AiSessionStartException("Windows sandbox 설정을 확인할 수 없어 비대화형 Codex 실행을 비활성화했습니다. Codex에서 sandbox를 설정한 뒤 다시 시도하세요.");
        }

        if (request.Mode == LaunchMode.Interactive && _env.TerminalLauncher is null)
        {
            throw new AiSessionStartException("대화형 터미널을 사용할 수 없습니다.");
        }

        if (request.Mode == LaunchMode.NonInteractive && string.IsNullOrWhiteSpace(request.Prompt))
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
                FinalPathResolver = _env.FinalPathResolver,
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
                Prompt = request.Mode == LaunchMode.NonInteractive ? request.Prompt : null,
                AllowReadOnlyShell = request.AllowReadOnlyShell,
                MaxBudgetUsd = request.MaxBudgetUsd,
                McpConfigPath = _env.StartMcpHost is null ? null : Path.Combine(directory, "mcp.json"),
                McpBridgeExecutable = _env.StartMcpHost is null ? null : _env.GuardExecutable,
                WindowsSandboxMode = windowsSandboxMode,
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

            // After the caller saved all documents: what the files look like before the AI touches them.
            session.SnapshotRoots = [.. scope.Projects
                .GroupBy(p => p.Root, StringComparer.OrdinalIgnoreCase)
                .Select(g => new SnapshotRoot(g.Key, projects.FirstOrDefault(d => d.Id == g.First().ProjectId) is { } definition
                    ? ExcludedFolderPolicy.For(definition)
                    : ExcludedFolderPolicy.None))];
            session.Snapshot = SessionSnapshots.Capture(
                session.SnapshotRoots, request.OpenDocuments, _env.Store, OpenBackups(session), _env.SnapshotLimits);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw new AiSessionStartException($"세션을 준비하지 못했습니다: {ex.Message}", ex);
        }

        // One CLI per conversation: a resumed conversation that is still running (or starting) elsewhere is refused,
        // checked and reserved atomically so two starts cannot both pass.
        ReserveConversation(request.Cli, conversation.Id);
        try
        {
            try
            {
                session.DirectoryLease = new FileStream(Path.Combine(directory, SessionRetention.LeaseFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                session.McpHost = _env.StartMcpHost?.Invoke(session);
                if (request.Mode == LaunchMode.Interactive)
                {
                    session.Terminal = new TerminalConnection(_env.TerminalLauncher!.Start(plan, TerminalSize.Default), TerminalSize.Default, plan.WorkingDirectory);
                }
                else
                {
                    session.Process = _env.Launcher.Start(plan);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                session.McpHost?.Dispose();
                session.DirectoryLease?.Dispose();
                throw new AiSessionStartException($"CLI를 실행하지 못했습니다: {ex.Message}", ex);
            }

            RememberConversation(scope, request.Cli, conversation.Id);
            session.StateChanged += _ => Changed?.Invoke(this, EventArgs.Empty);
            lock (_gate)
            {
                _sessions.Add(session);
            }
        }
        finally
        {
            ReleaseConversation(request.Cli, conversation.Id);
        }

        if (session.Process is { } process)
        {
            var stdout = PumpAsync(session, process.StandardOutput, isError: false);
            var stderr = PumpAsync(session, process.StandardError, isError: true);
            _ = WatchExitAsync(session, Task.WhenAll(stdout, stderr));
        }
        else
        {
            _ = WatchExitAsync(session, session.Terminal!.OutputEnded);
        }

        _ = WatchHeartbeatAsync(session);

        // Projects removed while the session was being prepared (on a worker thread) were not seen by
        // OnProjectsChanged: re-check now that the session is registered.
        OnProjectsChanged();
        Changed?.Invoke(this, EventArgs.Empty);
        return session;
    }

    private readonly HashSet<(CliKind, string)> _startingConversations = [];

    private void ReserveConversation(CliKind cli, string? conversationId)
    {
        if (conversationId is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_startingConversations.Contains((cli, conversationId))
                // Not only Running: a stopped or failed session whose process has not ended yet still owns it.
                || _sessions.Any(s => (s.State == AiSessionState.Running || !s.ProcessExited.IsCompleted) && s.Cli == cli
                    && string.Equals(s.ConversationId, conversationId, StringComparison.OrdinalIgnoreCase)))
            {
                throw new AiSessionStartException("이 대화는 다른 세션에서 실행 중입니다. 그 세션을 중지한 뒤 다시 시작하세요.");
            }

            _startingConversations.Add((cli, conversationId));
        }
    }

    private void ReleaseConversation(CliKind cli, string? conversationId)
    {
        if (conversationId is not null)
        {
            lock (_gate)
            {
                _startingConversations.Remove((cli, conversationId));
            }
        }
    }

    /// <summary>The session's own backup store (start capture and pre-write captures).</summary>
    public BackupStore OpenBackups(AiSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var backups = SessionSnapshots.OpenStore(_env.Store, session.Directory, _env.Time, _env.BackupProtector);
        backups.IsAutomaticBackupExcluded = _env.BackupExcluded;
        return backups;
    }

    /// <summary>Compares the scope now with the start snapshot ("변경 검토", also at the end of a session).</summary>
    public ChangeSummary? BuildChangeSummary(AiSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Snapshot is not { } start)
        {
            return null;
        }

        var now = ScopeIndex.Build(session.SnapshotRoots, _env.Store, _env.SnapshotLimits, _env.ReviewExcluded, _env.ReviewDirectoryExcluded);
        return ChangeSummaryBuilder.Build(start, now, OpenBackups(session), _env.Store, _env.SnapshotLimits, _env.ReviewExcluded);
    }

    /// <summary>Reverts changes of <paramref name="session"/>, limited to the roots indexed when it started.</summary>
    public RevertPlanner CreateRevertPlanner(AiSession session, SafeWriter writer)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(writer);
        return new RevertPlanner(_env.Store, writer, _env.RecycleBin ?? NoRecycleBin.Instance, _env.SnapshotLimits, session.Snapshot?.Roots ?? session.SnapshotRoots, _env.ReviewExcluded);
    }

    private sealed class NoRecycleBin : IRecycleBin
    {
        public static readonly NoRecycleBin Instance = new();

        public bool CanRecycle(string path) => false;

        public RecycleTicket Recycle(string path) => throw new IOException("휴지통을 사용할 수 없습니다.");

        public bool Contains(RecycleTicket ticket) => false;

        public void Restore(RecycleTicket ticket) => throw new IOException("휴지통을 사용할 수 없습니다.");
    }

    /// <summary>Stops a running session (terminates its whole process tree).</summary>
    public void Stop(AiSession session, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Finish(AiSessionState.Stopped, null, reason ?? "사용자가 중지했습니다."))
        {
            session.TerminateProcess();
        }
    }

    public void MarkClosed(AiSession session)
    {
        if (session.State != AiSessionState.Running)
            SessionRetention.MarkClosed(session.Directory, _env.Time.GetUtcNow());
    }

    /// <summary>
    /// Re-checks running sessions against the project list (call on <c>ProjectRegistry.Changed</c>). A project that
    /// was removed, became unselectable or moved to another root leaves the scope at once (scope.json is rewritten,
    /// the guard reads it on the next tool call). Losing the primary project stops the session.
    /// </summary>
    public void OnProjectsChanged()
    {
        lock (_scopeSync)
        {
            NarrowScopes();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Takes an additional project out of a running session's scope (scope chip "범위에서 제외"). scope.json is
    /// rewritten at once; the guard denies that folder from the next tool call. The primary project cannot leave.
    /// </summary>
    /// <returns>Null on success, else the reason (for the user).</returns>
    public string? ExcludeProject(AiSession session, string projectId)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_scopeSync)
        {
            if (session.State != AiSessionState.Running)
            {
                return "세션이 실행 중이 아닙니다.";
            }

            var narrowed = session.Scope.Without(projectId);
            if (narrowed is null || narrowed.Additional.Count == session.Scope.Additional.Count)
            {
                return narrowed is null ? "주 프로젝트는 범위에서 뺄 수 없습니다." : "이 프로젝트는 세션 범위에 없습니다.";
            }

            try
            {
                var generation = session.ScopeGeneration + 1;
                ScopeFile.Write(Path.Combine(session.Directory, SessionFileNames.Scope), ScopeDocumentFor(session, narrowed, generation));
                session.ScopeGeneration = generation;
                session.Scope = narrowed;
                if (session.Cli == CliKind.Codex)
                    Stop(session, "Codex 범위가 축소되어 중지했습니다. 새 범위로 세션을 다시 시작하세요.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Stop(session, $"범위 파일을 갱신하지 못해 세션을 중지했습니다: {ex.Message}");
                return $"범위 파일을 갱신하지 못해 세션을 중지했습니다: {ex.Message}";
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return null;
    }

    // One scope update at a time (UI thread and a starting session may both call OnProjectsChanged).
    private readonly Lock _scopeSync = new();

    private void NarrowScopes()
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
                if (session.Cli == CliKind.Codex)
                    Stop(session, "Codex 범위가 축소되어 중지했습니다. 새 범위로 세션을 다시 시작하세요.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Never leave a session running with a scope wider than the user's projects.
                Stop(session, $"범위 파일을 갱신하지 못해 세션을 중지했습니다: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        foreach (var session in Sessions)
        {
            Stop(session, "DotDeck이 종료되어 세션을 중지했습니다.");
            session.DisposeProcess();
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
            .Select(g => new ScopeFileRoot
            {
                ProjectId = g.First().ProjectId, Name = g.First().Name, Path = g.Key,
            })],
        ReadOnlyRoots = [.. ClaudeReadOnlyFolders.Select(f => Path.Combine(_env.Paths.ClaudeHome, f))],
        Home = _env.Paths.Home,
        ExclusionsFile = _env.Paths.ExclusionsFile,
    };

    private (string? Id, bool Resume) PickConversation(AiSessionStartRequest request, SessionScope scope)
    {
        if (request.Cli == CliKind.Codex)
        {
            if (request.ResumeConversation && request.Mode == LaunchMode.Interactive)
                throw new AiSessionStartException("Codex 대화형 세션은 새 대화로 시작하세요. 이어가기는 비대화형에서 지원합니다.");
            var previousId = request.ResumeConversationId ?? _env.Presets?.GetConversation(scope.Key)?.CodexThreadId;
            return request.ResumeConversation && Guid.TryParse(previousId, out var thread)
                ? (thread.ToString("D"), true) : (null, false);
        }

        if (request.ResumeConversation && request.ResumeConversationId is { } explicitId && Guid.TryParse(explicitId, out _))
        {
            return (Canonical(explicitId), true);
        }

        if (request.ResumeConversation && _env.Presets?.GetConversation(scope.Key)?.ClaudeSessionId is { } previous && Guid.TryParse(previous, out _))
        {
            return (Canonical(previous), true);
        }

        return (Guid.NewGuid().ToString("D"), false);

        // One spelling per conversation (lowercase "D" form) for the reservation, the command line and sessions.json.
        static string Canonical(string id) => Guid.Parse(id).ToString("D");
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

    private async Task PumpAsync(AiSession session, Stream stream, bool isError)
    {
        try
        {
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (!isError && session.Cli == CliKind.Codex && CodexJsonlParser.ThreadId(line) is { } threadId)
                {
                    session.ConversationId = threadId;
                    RememberConversation(session.Scope, session.Cli, threadId);
                }
                if (!isError && session.Cli == CliKind.Codex && CodexJsonlParser.TerminalFailure(line) is { } failure)
                    session.CliFailure = failure;
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
    private async Task WatchExitAsync(AiSession session, Task outputRead)
    {
        var code = await session.ProcessExited.ConfigureAwait(false);
        session.TerminateProcess();
        await outputRead.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        if (session.CliFailure is { } failure)
        {
            session.Finish(AiSessionState.Failed, code, failure);
        }
        else if (session.Cli == CliKind.Claude && !File.Exists(Path.Combine(session.Directory, Guard.GuardHost.HeartbeatFileName)))
        {
            session.Finish(AiSessionState.Failed, code, HeartbeatMissing);
        }
        else
        {
            session.Finish(code == 0 ? AiSessionState.Exited : AiSessionState.Failed, code, code == 0 ? null : $"CLI가 종료 코드 {code}로 끝났습니다.");
        }

        // A terminal keeps its recent output after this (a viewer can still attach and replay it).
        session.DisposeProcess();
    }

    private static readonly TimeSpan HeartbeatPoll = TimeSpan.FromMilliseconds(250);

    private const string HeartbeatMissing = "세션 설정(가드 훅)이 적용되지 않아 세션을 중지했습니다. CLI 버전과 설정을 확인하세요.";

    private async Task WatchHeartbeatAsync(AiSession session)
    {
        if (session.Cli != CliKind.Claude)
        {
            return;
        }

        var heartbeat = Path.Combine(session.Directory, Guard.GuardHost.HeartbeatFileName);
        var exited = session.ProcessExited;
        var timeout = Task.Delay(_env.HeartbeatTimeout, _env.Time);
        while (!timeout.IsCompleted && !exited.IsCompleted)
        {
            if (File.Exists(heartbeat))
            {
                session.MarkGuardConnected();
                return;
            }

            await Task.WhenAny(exited, timeout, Task.Delay(HeartbeatPoll, _env.Time)).ConfigureAwait(false);
        }

        if (File.Exists(heartbeat))
        {
            session.MarkGuardConnected();
            return;
        }

        if (exited.IsCompleted)
        {
            return; // WatchExitAsync decides (a run that never loaded the settings is a failure there).
        }

        if (session.Finish(AiSessionState.Failed, null, HeartbeatMissing))
        {
            session.TerminateProcess();
        }
    }
}
