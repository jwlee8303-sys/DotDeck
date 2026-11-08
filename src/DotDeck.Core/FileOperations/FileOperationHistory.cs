namespace DotDeck.Core.FileOperations;

/// <summary>One undoable (or redoable) entry: the steps that reverse an earlier operation.</summary>
public sealed class FileOperationEntry
{
    internal FileOperationEntry(string description, IReadOnlyList<FileStep> steps)
    {
        Description = description;
        Steps = steps;
    }

    /// <summary>What the original operation did ("이름 바꾸기 'a' → 'b'").</summary>
    public string Description { get; }

    internal IReadOnlyList<FileStep> Steps { get; }
}

/// <summary>
/// Session-only undo/redo stacks for file operations (DESIGN Q10: one global stack, capacity 100).
/// Thread-safe; <see cref="Changed"/> is raised on the thread that changed the history.
/// </summary>
public sealed class FileOperationHistory
{
    public const int DefaultCapacity = 100;

    private readonly LinkedList<FileOperationEntry> _undo = new();
    private readonly Stack<FileOperationEntry> _redo = new();
    private readonly Lock _gate = new();
    private readonly int _capacity;

    public FileOperationHistory(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    public event EventHandler? Changed;

    public bool CanUndo
    {
        get
        {
            lock (_gate)
            {
                return _undo.Count > 0;
            }
        }
    }

    public bool CanRedo
    {
        get
        {
            lock (_gate)
            {
                return _redo.Count > 0;
            }
        }
    }

    public int UndoCount
    {
        get
        {
            lock (_gate)
            {
                return _undo.Count;
            }
        }
    }

    public string? UndoDescription
    {
        get
        {
            lock (_gate)
            {
                return _undo.Last?.Value.Description;
            }
        }
    }

    public string? RedoDescription
    {
        get
        {
            lock (_gate)
            {
                return _redo.TryPeek(out var entry) ? entry.Description : null;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _undo.Clear();
            _redo.Clear();
        }

        OnChanged();
    }

    /// <summary>Records a new operation; anything that could be redone is discarded.</summary>
    internal void Record(FileOperationEntry entry)
    {
        lock (_gate)
        {
            _redo.Clear();
            PushUndoLocked(entry);
        }

        OnChanged();
    }

    internal FileOperationEntry? TakeUndo()
    {
        lock (_gate)
        {
            if (_undo.Last is not { } last)
            {
                return null;
            }

            _undo.RemoveLast();
            return last.Value;
        }
    }

    internal FileOperationEntry? TakeRedo()
    {
        lock (_gate)
        {
            return _redo.TryPop(out var entry) ? entry : null;
        }
    }

    /// <summary>Puts an entry (back) on the undo stack without touching redo.</summary>
    internal void PushUndo(FileOperationEntry entry)
    {
        lock (_gate)
        {
            PushUndoLocked(entry);
        }
    }

    internal void PushRedo(FileOperationEntry entry)
    {
        lock (_gate)
        {
            _redo.Push(entry);
        }
    }

    internal void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private void PushUndoLocked(FileOperationEntry entry)
    {
        _undo.AddLast(entry);
        while (_undo.Count > _capacity)
        {
            _undo.RemoveFirst();
        }
    }
}
