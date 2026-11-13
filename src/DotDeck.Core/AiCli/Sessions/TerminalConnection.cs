using System.Text;
using System.Threading.Channels;

namespace DotDeck.Core.AiCli.Sessions;

/// <summary>
/// Connects an <see cref="ITerminalProcess"/> to its viewers: a dedicated reader decodes the console output (UTF-8,
/// sequences split across reads are kept whole) and keeps the most recent part for a viewer that attaches later or
/// is re-created (a tab moved between panes); a single writer task sends typed input so the UI never blocks on the
/// console.
/// </summary>
public sealed class TerminalConnection : IDisposable
{
    /// <summary>Characters of recent output replayed to a new viewer (DESIGN: 2MB raw replay buffer).</summary>
    public const int ReplayChars = 1024 * 1024;

    private readonly ITerminalProcess _process;
    private readonly Lock _gate = new();
    private readonly StringBuilder _replay = new();
    private readonly Channel<byte[]> _input = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource _outputEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Action<string>[] _viewers = [];
    private TerminalSize _size;
    private int _disposed;
    private string? _workingDirectory;
    private readonly StringBuilder _osc = new();
    private bool _escape;
    private bool _inOsc;

    public TerminalConnection(ITerminalProcess process, TerminalSize initialSize, string? workingDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        _process = process;
        _size = initialSize;
        _workingDirectory = workingDirectory;
        var reader = new Thread(ReadOutput) { IsBackground = true, Name = "DotDeck terminal output" };
        reader.Start();
        _ = WriteInputAsync();
    }

    public int ProcessId => _process.ProcessId;

    /// <summary>Latest OSC 7 file location, falling back to the launch directory.</summary>
    public string? WorkingDirectory
    {
        get { lock (_gate) return _workingDirectory; }
    }

    /// <summary>Completes with the exit code when the main process ends.</summary>
    public Task<int> Exited => _process.Exited;

    /// <summary>Completes when the output has ended (after the exit, everything printed has been delivered).</summary>
    public Task OutputEnded => _outputEnded.Task;

    public TerminalSize Size
    {
        get
        {
            lock (_gate)
            {
                return _size;
            }
        }
    }

    /// <summary>
    /// Returns the recent output and subscribes <paramref name="viewer"/> to what follows, atomically: nothing is
    /// lost or repeated. The callback runs on the reader thread and must not block.
    /// </summary>
    public string Attach(Action<string> viewer)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        lock (_gate)
        {
            _viewers = [.. _viewers, viewer];
            return _replay.ToString();
        }
    }

    public void Detach(Action<string> viewer)
    {
        lock (_gate)
        {
            _viewers = [.. _viewers.Where(v => v != viewer)];
        }
    }

    /// <summary>Queues typed text (keys arrive from the terminal page already as VT sequences).</summary>
    public void Send(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            _input.Writer.TryWrite(Encoding.UTF8.GetBytes(text));
        }
    }

    /// <summary>Queues raw bytes (xterm.js "binary" data such as legacy mouse reports: one char per byte).</summary>
    public void SendBinary(string latin1)
    {
        if (!string.IsNullOrEmpty(latin1))
        {
            _input.Writer.TryWrite(Encoding.Latin1.GetBytes(latin1));
        }
    }

    /// <summary>Resizes the console when the size actually changed.</summary>
    public void Resize(TerminalSize size)
    {
        lock (_gate)
        {
            if (_size == size)
            {
                return;
            }

            _size = size;
        }

        _process.Resize(size);
    }

    public void Terminate() => _process.Terminate();

    public IReadOnlyList<int> JobProcessIds() => _process.JobProcessIds();

    private void ReadOutput()
    {
        var decoder = new UTF8Encoding(false).GetDecoder();
        var bytes = new byte[16 * 1024];
        var chars = new char[decoder.GetCharCount(bytes, 0, bytes.Length, flush: false) + 4];
        try
        {
            int read;
            while ((read = _process.Output.Read(bytes, 0, bytes.Length)) > 0)
            {
                var count = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
                if (count > 0)
                {
                    Deliver(new string(chars, 0, count));
                }
            }

            var rest = decoder.GetChars(bytes, 0, 0, chars, 0, flush: true);
            if (rest > 0)
            {
                Deliver(new string(chars, 0, rest));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Disposed or the console went away: the output simply ends.
        }
        finally
        {
            _outputEnded.TrySetResult();
        }
    }

    private void Deliver(string text)
    {
        Action<string>[] viewers;
        lock (_gate)
        {
            TrackWorkingDirectory(text);
            _replay.Append(text);
            if (_replay.Length > ReplayChars)
            {
                // Cut at a line start where possible so the replay does not begin inside an escape sequence.
                var cut = _replay.Length - ReplayChars;
                var limit = Math.Min(_replay.Length, cut + 4096);
                for (var i = cut; i < limit; i++)
                {
                    if (_replay[i] == '\n')
                    {
                        cut = i + 1;
                        break;
                    }
                }

                _replay.Remove(0, cut);
            }

            viewers = _viewers;
        }

        foreach (var viewer in viewers)
        {
            viewer(text);
        }
    }

    // Stateful and bounded: OSC commands may be split across arbitrary UTF-8 output reads.
    private void TrackWorkingDirectory(string text)
    {
        foreach (var ch in text)
        {
            if (_inOsc)
            {
                if (ch == '\a' || (_escape && ch == '\\'))
                {
                    var value = _osc.ToString();
                    if (value.StartsWith("7;", StringComparison.Ordinal)
                        && Uri.TryCreate(value[2..], UriKind.Absolute, out var uri)
                        && uri.IsFile && (uri.IsLoopback || uri.IsUnc)
                        && Path.IsPathFullyQualified(uri.LocalPath)
                        && !uri.LocalPath.Any(char.IsControl))
                    {
                        _workingDirectory = uri.LocalPath;
                    }

                    _osc.Clear();
                    _inOsc = false;
                    _escape = false;
                    continue;
                }

                if (_escape) _osc.Append('\u001b');
                _escape = ch == '\u001b';
                if (!_escape) _osc.Append(ch);
                if (_osc.Length > 32768)
                {
                    _osc.Clear();
                    _inOsc = false;
                    _escape = false;
                }
            }
            else
            {
                _inOsc = _escape && ch == ']';
                _escape = !_inOsc && ch == '\u001b';
            }
        }
    }

    private async Task WriteInputAsync()
    {
        try
        {
            await foreach (var bytes in _input.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await _process.Input.WriteAsync(bytes).ConfigureAwait(false);
                await _process.Input.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException)
        {
            // The console ended; later input is dropped.
            _input.Writer.TryComplete();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _input.Writer.TryComplete();
        lock (_gate)
        {
            _viewers = [];
        }

        _process.Dispose();
    }
}
