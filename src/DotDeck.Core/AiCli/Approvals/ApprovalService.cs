using System.Globalization;

namespace DotDeck.Core.AiCli.Approvals;

/// <summary>
/// The single owner of approval requests: their state and the delivery of answers. AI 모니터 and notification cards
/// only show its state; every answer goes through <see cref="RespondAsync"/>, which lets exactly one answer through per
/// request (the state moves to <see cref="ApprovalState.Responding"/> under the lock before anything is sent).
/// Thread-safe; <see cref="Changed"/> is raised outside the lock on any thread.
/// </summary>
public sealed class ApprovalService : IDisposable
{
    /// <summary>How often wait limits are checked.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly IApprovalLog? _log;
    private readonly List<Entry> _open = [];
    private readonly Dictionary<string, ApprovalRequest> _last = new(StringComparer.Ordinal);
    private ApprovalSettings _settings;
    private ITimer? _timer;
    private bool _disposed;

    public ApprovalService(ApprovalSettings settings, TimeProvider? time = null, IApprovalLog? log = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _time = time ?? TimeProvider.System;
        _log = log;
    }

    /// <summary>Any request or setting changed.</summary>
    public event EventHandler? Changed;

    public ApprovalSettings Settings
    {
        get
        {
            lock (_gate)
            {
                return _settings;
            }
        }
    }

    public TimeProvider Time => _time;

    /// <summary>Requests that still wait for an answer from DotDeck (pending or being answered), oldest first.</summary>
    public IReadOnlyList<ApprovalRequest> Waiting
    {
        get
        {
            lock (_gate)
            {
                return [.. _open.Where(e => e.Request.IsWaiting).Select(e => e.Request)];
            }
        }
    }

    /// <summary>The request shown for a session: the oldest waiting one, else one handed to the terminal.</summary>
    public ApprovalRequest? CurrentFor(string sessionKey)
    {
        lock (_gate)
        {
            return _open.Where(e => e.Request.SessionKey == sessionKey && e.Request.IsWaiting).Select(e => e.Request).FirstOrDefault()
                ?? _open.Where(e => e.Request.SessionKey == sessionKey).Select(e => e.Request).FirstOrDefault();
        }
    }

    /// <summary>Number of waiting requests of a session (one is shown; answering moves to the next).</summary>
    public int WaitingCount(string sessionKey)
    {
        lock (_gate)
        {
            return _open.Count(e => e.Request.SessionKey == sessionKey && e.Request.IsWaiting);
        }
    }

    /// <summary>The last finished request of a session ("허용됨 · 21:30"), or null.</summary>
    public ApprovalRequest? LastFor(string sessionKey)
    {
        lock (_gate)
        {
            return _last.GetValueOrDefault(sessionKey);
        }
    }

    public ApprovalRequest? Find(string id)
    {
        lock (_gate)
        {
            return _open.FirstOrDefault(e => e.Request.Id == id)?.Request;
        }
    }

    /// <summary>Registers a new request; a hook request waits at most <see cref="ApprovalSettings.HookWaitLimit"/>.</summary>
    public void Add(ApprovalRequest request, IApprovalChannel channel)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(channel);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (request.Source == ApprovalSource.Hook)
            {
                request.WaitLimit = request.RequestedAt + ApprovalSettings.HookWaitLimit;
            }

            // A new prompt of the session means an earlier one handed to the terminal was answered there (a denial
            // there sends no other signal).
            foreach (var handed in _open.Where(e => e.Request.SessionKey == request.SessionKey && e.Request.State == ApprovalState.HandedToTerminal).ToList())
            {
                Finish(handed, ApprovalState.AnsweredInTerminal, "터미널에서 응답됨", ApprovalResponder.Terminal);
            }

            _open.Add(new Entry(request, channel));
            EnsureTimer();
        }

        OnChanged();
    }

    /// <summary>
    /// Answers a request once. Re-checks the state (an answered, handed off or expired request gets nothing sent) and
    /// the CLI side (<see cref="IApprovalChannel.SendAsync"/>). True when the decision was delivered.
    /// </summary>
    public async Task<bool> RespondAsync(string id, ApprovalDecision decision, ApprovalResponder responder, CancellationToken cancellationToken = default)
    {
        Entry? entry;
        lock (_gate)
        {
            entry = _open.FirstOrDefault(e => e.Request.Id == id);
            if (entry is null || entry.Request.State != ApprovalState.Pending || !entry.Request.CanRespond)
            {
                return false;
            }

            entry.Request.State = ApprovalState.Responding;
        }

        OnChanged();
        bool sent;
        try
        {
            sent = await entry.Channel.SendAsync(decision, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException or TimeoutException)
        {
            sent = false;
        }

        lock (_gate)
        {
            if (entry.Request.State != ApprovalState.Responding)
            {
                // Resolved meanwhile (the terminal answer arrived first): keep that result.
                return false;
            }

            if (sent)
            {
                var state = decision == ApprovalDecision.Allow ? ApprovalState.Allowed : ApprovalState.Denied;
                Finish(entry, state, ResultFor(state), responder);
            }
            else
            {
                Finish(entry, ApprovalState.Expired, "응답하지 못함 · 이미 처리됐거나 프롬프트가 사라짐", null);
            }
        }

        OnChanged();
        return sent;
    }

    /// <summary>DotDeck stops waiting (the user is at the session's terminal); the CLI's own prompt decides.</summary>
    public bool HandOff(string id)
    {
        lock (_gate)
        {
            var entry = _open.FirstOrDefault(e => e.Request.Id == id);
            if (entry is null || entry.Request.State != ApprovalState.Pending)
            {
                return false;
            }

            entry.Request.State = ApprovalState.HandedToTerminal;
            entry.Request.ResultText = "터미널에서 응답 중";
            entry.Channel.Release();
        }

        OnChanged();
        return true;
    }

    /// <summary>The request was answered in the terminal (or disappeared from it).</summary>
    public bool MarkAnsweredInTerminal(string id, string resultText)
    {
        lock (_gate)
        {
            var entry = _open.FirstOrDefault(e => e.Request.Id == id);
            if (entry is null || entry.Request.State is not (ApprovalState.Pending or ApprovalState.HandedToTerminal))
            {
                return false;
            }

            Finish(entry, ApprovalState.AnsweredInTerminal, resultText, ApprovalResponder.Terminal);
        }

        OnChanged();
        return true;
    }

    /// <summary>
    /// The hook of a request ended on its own while DotDeck still waited. Claude Code ends the hook when the prompt is
    /// rejected in the terminal (verified with 2.1.330); a request DotDeck released itself is not affected.
    /// </summary>
    public bool HookEnded(string id)
    {
        lock (_gate)
        {
            var entry = _open.FirstOrDefault(e => e.Request.Id == id);
            if (entry is null || entry.Request.State != ApprovalState.Pending)
            {
                return false;
            }

            Finish(entry, ApprovalState.AnsweredInTerminal, "터미널에서 거부·취소됨", ApprovalResponder.Terminal);
        }

        OnChanged();
        return true;
    }

    /// <summary>
    /// A hook-reported tool call ran (PostToolUse): the oldest open request of the session with the same tool call is
    /// answered — in the terminal unless DotDeck already answered it.
    /// </summary>
    public bool MarkToolRan(string sessionKey, string matchKey)
    {
        string? id;
        lock (_gate)
        {
            id = _open.FirstOrDefault(e => e.Request.SessionKey == sessionKey && e.Request.MatchKey == matchKey
                && e.Request.State is ApprovalState.Pending or ApprovalState.HandedToTerminal)?.Request.Id;
        }

        return id is not null && MarkAnsweredInTerminal(id, "허용됨(터미널)");
    }

    /// <summary>The request can no longer be answered from DotDeck.</summary>
    public bool Expire(string id, string resultText)
    {
        lock (_gate)
        {
            var entry = _open.FirstOrDefault(e => e.Request.Id == id);
            if (entry is null || entry.Request.State == ApprovalState.Responding)
            {
                return false;
            }

            Finish(entry, ApprovalState.Expired, resultText, null);
        }

        OnChanged();
        return true;
    }

    /// <summary>The session ended: its requests are dropped (the CLI is gone).</summary>
    public void SessionEnded(string sessionKey)
    {
        var changed = false;
        lock (_gate)
        {
            foreach (var entry in _open.Where(e => e.Request.SessionKey == sessionKey && e.Request.State != ApprovalState.Responding).ToList())
            {
                Finish(entry, ApprovalState.Expired, "세션 종료", null);
                changed = true;
            }

            changed |= _last.Remove(sessionKey);
        }

        if (changed)
        {
            OnChanged();
        }
    }

    /// <summary>Applies new settings. Turning detection off hands every open request back to its CLI prompt.</summary>
    public void ApplySettings(ApprovalSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            _settings = settings;
            foreach (var entry in _open.ToList())
            {
                var request = entry.Request;
                if (!_settings.Detects(request.Cli) || (request.Source == ApprovalSource.Screen && !_settings.ScreenDetection))
                {
                    if (request.State == ApprovalState.Pending)
                    {
                        request.State = ApprovalState.HandedToTerminal;
                        request.ResultText = "감지 꺼짐 · 터미널에서 응답";
                        entry.Channel.Release();
                    }
                }
            }
        }

        OnChanged();
    }

    /// <summary>Hands requests past their wait limit to the CLI prompt (the internal timer calls it; tests call it directly).</summary>
    public void Tick()
    {
        var now = _time.GetUtcNow();
        var expired = false;
        lock (_gate)
        {
            foreach (var entry in _open.ToList())
            {
                var request = entry.Request;
                if (request.State != ApprovalState.Pending)
                {
                    continue;
                }

                if (request.WaitLimit is { } limit && now >= limit)
                {
                    Finish(entry, ApprovalState.Expired, "대기 시간 초과 · 터미널에서 응답", null);
                    expired = true;
                }
            }

            if (!_open.Any(e => e.Request.State == ApprovalState.Pending && e.Request.WaitLimit is not null))
            {
                _timer?.Dispose();
                _timer = null;
            }
        }

        if (expired)
        {
            OnChanged();
        }
    }

    public void Dispose()
    {
        List<Entry> open;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            open = [.. _open];
            _open.Clear();
        }

        // DotDeck is closing: every hook ends without a decision (fail-safe, the CLI prompt decides).
        foreach (var entry in open)
        {
            entry.Channel.Release();
        }
    }

    /// <summary>Formats a result line ("허용됨 · 21:30").</summary>
    public static string FormatResult(ApprovalRequest request) =>
        request.ResolvedAt is { } at
            ? (request.ResultText ?? string.Empty) + " · " + at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
            : request.ResultText ?? string.Empty;

    private void Finish(Entry entry, ApprovalState state, string resultText, ApprovalResponder? responder)
    {
        var request = entry.Request;
        request.State = state;
        request.ResolvedAt = _time.GetUtcNow();
        request.ResultText = resultText;
        _open.Remove(entry);
        _last[request.SessionKey] = request;

        // A hook still waiting (terminal answer, expiry) ends without a decision.
        if (state is not (ApprovalState.Allowed or ApprovalState.Denied))
        {
            entry.Channel.Release();
        }

        if (responder is { } who)
        {
            try
            {
                _log?.Record(request, who);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The record is best effort; the answer itself already happened.
            }
        }
    }

    private static string ResultFor(ApprovalState state) => state switch
    {
        ApprovalState.Allowed => "허용됨",
        ApprovalState.Denied => "거부됨",
        _ => string.Empty,
    };

    private void EnsureTimer() => _timer ??= _time.CreateTimer(_ => Tick(), null, TickInterval, TickInterval);

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private sealed record Entry(ApprovalRequest Request, IApprovalChannel Channel);
}
