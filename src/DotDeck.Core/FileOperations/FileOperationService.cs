using DotDeck.Core.Documents;
using DotDeck.Core.Explorer;
using DotDeck.Core.Files;

namespace DotDeck.Core.FileOperations;

/// <summary>Outcome of an operation, undo or redo.</summary>
/// <param name="Paths">Items that exist after the operation (to select in the tree).</param>
/// <param name="Warnings">Things the user should know (e.g. a recycled item DotDeck cannot restore).</param>
public sealed record FileOperationResult(string Description, IReadOnlyList<string> Paths, IReadOnlyList<string> Warnings)
{
    /// <summary>What changed on disk, in order (rules, pins and open tabs follow these).</summary>
    public IReadOnlyList<PathChange> Changes { get; init; } = [];
}

/// <summary>
/// Explorer file operations: create, rename, move, copy, duplicate and delete (to the recycle bin), each recorded
/// in <see cref="FileOperationHistory"/> for undo/redo. Nothing is overwritten except by an explicit paste choice
/// (the old file is backed up and sent to the recycle bin), and DotDeck never deletes permanently by itself (see <see cref="IRecycleBin"/>).
/// Calls are synchronous (run them off the UI thread) and serialized.
/// Failures throw <see cref="FileOperationException"/> with a message for the user.
/// </summary>
public sealed class FileOperationService
{
    private readonly FileStepContext _context;
    private readonly Lock _sync = new();

    private readonly BackupStore? _backups;

    /// <param name="backups">Where a file replaced by "덮어쓰기" is backed up first (SPEC 6); null skips the backup.</param>
    public FileOperationService(IFileStore store, IRecycleBin recycleBin, FileOperationHistory history, BackupStore? backups = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(recycleBin);
        ArgumentNullException.ThrowIfNull(history);
        _context = new FileStepContext(store, recycleBin);
        History = history;
        _backups = backups;
    }

    public FileOperationHistory History { get; }

    private IFileStore Store => _context.Store;

    public FileOperationResult CreateFile(string directory, string name) => Create(directory, name, isDirectory: false);

    public FileOperationResult CreateFolder(string directory, string name) => Create(directory, name, isDirectory: true);

    public FileOperationResult Rename(string path, string newName)
    {
        ThrowIfInvalidName(newName);
        lock (_sync)
        {
            var from = FilePaths.Normalize(path);
            var parent = Path.GetDirectoryName(from) ?? throw new FileOperationException("드라이브 루트는 이름을 바꿀 수 없습니다.");
            var to = Path.Combine(parent, newName);
            if (string.Equals(from, to, StringComparison.Ordinal))
            {
                return new FileOperationResult("이름 바꾸기", [from], []);
            }

            var description = $"이름 바꾸기 '{Path.GetFileName(from)}' → '{newName}'";
            return Run(description, [new MoveStep(from, to)]);
        }
    }

    /// <summary>Moves items into <paramref name="targetDirectory"/>. Items already there are skipped.</summary>
    public FileOperationResult Move(IReadOnlyList<string> sources, string targetDirectory)
    {
        lock (_sync)
        {
            var target = RequireDirectory(targetDirectory);
            var steps = new List<FileStep>();
            foreach (var source in NormalizeSources(sources))
            {
                if (FilePaths.AreSame(Path.GetDirectoryName(source)!, target))
                {
                    continue;
                }

                // Directory.Move cannot cross volumes; a copy + delete would need its own undo story.
                if (Store.DirectoryExists(source)
                    && !string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase))
                {
                    throw new FileOperationException($"폴더는 다른 드라이브로 이동할 수 없습니다. 복사한 뒤 원본을 삭제하세요: {source}");
                }

                var destination = Path.Combine(target, Path.GetFileName(source));
                if (steps.OfType<MoveStep>().Any(s => FilePaths.AreSame(s.To, destination)))
                {
                    // Two selected items with the same name: refuse up front instead of failing halfway.
                    throw new FileOperationException($"같은 이름의 항목 여러 개를 한 폴더로 옮길 수 없습니다: {Path.GetFileName(source)}");
                }

                steps.Add(new MoveStep(source, destination));
            }

            return steps.Count == 0
                ? new FileOperationResult("이동", [], [])
                : Run(CountDescription("이동", steps.Count), steps);
        }
    }

    /// <summary>Copies items into <paramref name="targetDirectory"/>; a taken name becomes "… - 복사본".</summary>
    public FileOperationResult Copy(IReadOnlyList<string> sources, string targetDirectory)
    {
        lock (_sync)
        {
            var target = RequireDirectory(targetDirectory);
            var items = NormalizeSources(sources).Select(s => (Source: s, Target: target)).ToList();
            return CopyCore("복사", items);
        }
    }

    /// <summary>Copies each item next to itself ("a - 복사본.txt").</summary>
    public FileOperationResult Duplicate(IReadOnlyList<string> sources)
    {
        lock (_sync)
        {
            var items = NormalizeSources(sources)
                .Select(s => (Source: s, Target: Path.GetDirectoryName(s) ?? throw new FileOperationException("드라이브 루트는 복제할 수 없습니다.")))
                .ToList();
            return CopyCore("복제", items);
        }
    }

    /// <summary>True when every item can go to the recycle bin (otherwise <see cref="Delete"/> refuses).</summary>
    public bool CanRecycleAll(IReadOnlyList<string> paths) =>
        NormalizeSources(paths).All(_context.RecycleBin.CanRecycle);

    /// <summary>Sends items to the recycle bin. Refused as a whole if any item has no recycle bin.</summary>
    public FileOperationResult Delete(IReadOnlyList<string> paths)
    {
        lock (_sync)
        {
            var steps = NormalizeSources(paths).Select(p => (FileStep)new RecycleStep(p)).ToList();
            if (steps.Count == 0)
            {
                return new FileOperationResult("삭제", [], []);
            }

            return Run(CountDescription("삭제", steps.Count), steps);
        }
    }

    /// <summary>Reverses the latest operation, or returns null when there is nothing to undo.</summary>
    public FileOperationResult? Undo()
    {
        lock (_sync)
        {
            return History.TakeUndo() is { } entry
                ? Replay(entry, History.PushUndo, History.PushRedo, "실행 취소")
                : null;
        }
    }

    /// <summary>Re-applies the latest undone operation, or returns null when there is nothing to redo.</summary>
    public FileOperationResult? Redo()
    {
        lock (_sync)
        {
            return History.TakeRedo() is { } entry
                ? Replay(entry, History.PushRedo, History.PushUndo, "다시 실행")
                : null;
        }
    }

    private FileOperationResult Create(string directory, string name, bool isDirectory)
    {
        ThrowIfInvalidName(name);
        lock (_sync)
        {
            var parent = RequireDirectory(directory);
            var path = Path.Combine(parent, name);
            if (Store.GetEntry(path) is not null)
            {
                throw new FileOperationException($"같은 이름의 항목이 이미 있습니다: {path}");
            }

            try
            {
                if (isDirectory)
                {
                    Store.CreateDirectory(path);
                }
                else
                {
                    Store.CreateFile(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new FileOperationException($"만들지 못했습니다: {path}\n{ex.Message}", innerException: ex);
            }

            var description = isDirectory ? $"새 폴더 '{name}'" : $"새 파일 '{name}'";
            History.Record(new FileOperationEntry(description, [new RecycleStep(path)]));
            return new FileOperationResult(description, [path], []) { Changes = [new PathChange.Created(path)] };
        }
    }

    private FileOperationResult CopyCore(string verb, List<(string Source, string Target)> items)
    {
        // Validate everything first so a refused copy changes nothing.
        foreach (var (source, target) in items)
        {
            if (Store.GetEntry(source) is null)
            {
                throw new FileOperationException($"'{Path.GetFileName(source)}'이(가) 더 이상 없습니다: {source}");
            }

            if (FilePaths.IsInside(target, source) || FilePaths.AreSame(target, source))
            {
                throw new FileOperationException("폴더를 자기 자신 안으로 복사할 수 없습니다.");
            }
        }

        var undoSteps = new List<FileStep>();
        var created = new List<string>();
        try
        {
            foreach (var (source, target) in items)
            {
                var isDirectory = Store.DirectoryExists(source);
                var name = Path.GetFileName(source);
                if (Store.GetEntry(Path.Combine(target, name)) is not null)
                {
                    name = FileNameRules.MakeCopyName(name, isDirectory, n => Store.GetEntry(Path.Combine(target, n)) is not null);
                }

                var destination = Path.Combine(target, name);
                try
                {
                    Store.Copy(source, destination);
                }
                catch (PartialCopyException partial) when (Store.GetEntry(partial.Destination) is not null)
                {
                    // The store reports only a partial copy it created itself: make it undoable too.
                    undoSteps.Add(new RecycleStep(partial.Destination));
                    throw;
                }

                undoSteps.Add(new RecycleStep(destination));
                created.Add(destination);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            undoSteps.Reverse();
            var partial = RecordIfAny(CountDescription(verb, items.Count), undoSteps);
            throw new FileOperationException(FailureMessage(verb, created.Count, items.Count, ex), partial, ex);
        }

        var description = CountDescription(verb, items.Count);
        undoSteps.Reverse();
        History.Record(new FileOperationEntry(description, undoSteps));
        return new FileOperationResult(description, created, []) { Changes = created.Select(c => (PathChange)new PathChange.Created(c)).ToList() };
    }

    /// <summary>Checks and applies forward steps, then records their inverses as one undo entry.</summary>
    private FileOperationResult Run(string description, List<FileStep> steps)
    {
        ThrowIfAnyCheckFails(steps);
        var (inverses, paths, warnings, changes, error) = Apply(steps);
        if (error is not null)
        {
            var partial = RecordIfAny(description, inverses);
            throw new FileOperationException(FailureMessage(description, inverses.Count, steps.Count, error), partial, error) { Changes = changes };
        }

        History.Record(new FileOperationEntry(description, inverses));
        return new FileOperationResult(description, paths, warnings) { Changes = changes };
    }

    /// <summary>
    /// Runs an undo or redo entry. On success its inverse goes to the other stack. If it fails halfway, the
    /// applied part moves to the other stack and the rest stays, so the history always matches the disk.
    /// </summary>
    private FileOperationResult Replay(
        FileOperationEntry entry,
        Action<FileOperationEntry> pushBack,
        Action<FileOperationEntry> pushOpposite,
        string verb)
    {
        try
        {
            ThrowIfAnyCheckFails(entry.Steps);
        }
        catch
        {
            pushBack(entry);
            History.OnChanged();
            throw;
        }

        var (inverses, paths, warnings, changes, error) = Apply(entry.Steps);
        if (inverses.Count > 0)
        {
            pushOpposite(new FileOperationEntry(entry.Description, inverses));
        }

        if (error is not null)
        {
            var remaining = entry.Steps.Skip(inverses.Count).ToList();
            pushBack(new FileOperationEntry(entry.Description, remaining));
            History.OnChanged();
            throw new FileOperationException(
                FailureMessage($"{verb}: {entry.Description}", inverses.Count, entry.Steps.Count, error),
                inverses.Count > 0,
                error) { Changes = changes };
        }

        History.OnChanged();
        return new FileOperationResult($"{verb}: {entry.Description}", paths, warnings) { Changes = changes };
    }

    /// <summary>Applies steps in order and returns the inverses in undo order (last applied first).</summary>
    private (List<FileStep> Inverses, List<string> Paths, List<string> Warnings, List<PathChange> Changes, Exception? Error) Apply(IReadOnlyList<FileStep> steps)
    {
        var inverses = new List<FileStep>(steps.Count);
        var paths = new List<string>();
        var warnings = new List<string>();
        var changes = new List<PathChange>();
        Exception? error = null;
        foreach (var step in steps)
        {
            try
            {
                // Re-check right before acting: an earlier step or another program may have changed things.
                if (step.Check(_context) is { } problem)
                {
                    error = new FileOperationException(problem);
                    break;
                }

                var inverse = step.Apply(_context);
                inverses.Insert(0, inverse);
                changes.Add(ChangeOf(step));
                if (step.ResultPath is { } path)
                {
                    paths.Add(path);
                }

                if (inverse is RestoreStep { Ticket.CanRestore: false } restore)
                {
                    warnings.Add($"'{Path.GetFileName(restore.Ticket.OriginalPath)}'은(는) 휴지통에서 위치를 찾지 못해 DotDeck에서 되돌릴 수 없습니다(Windows 휴지통을 확인하세요. 셸의 영구 삭제 확인을 승인했다면 복원할 수 없습니다).");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex;
                break;
            }
        }

        return (inverses, paths, warnings, changes, error);
    }

    private static PathChange ChangeOf(FileStep step) => step switch
    {
        MoveStep move => new PathChange.Moved(move.From, move.To),
        RecycleStep recycle => new PathChange.Removed(recycle.Path),
        RestoreStep restore => new PathChange.Created(restore.Ticket.OriginalPath),
        _ => throw new InvalidOperationException($"Unknown step {step.GetType().Name}"),
    };

    /// <summary>
    /// Checks a paste into <paramref name="targetDirectory"/> and lists name conflicts for the user to resolve.
    /// Refuses a folder pasted into itself or a folder below it, and a folder moved to another drive.
    /// </summary>
    public PastePlan PlanPaste(IReadOnlyList<string> sources, string targetDirectory, bool isMove)
    {
        lock (_sync)
        {
            var target = RequireDirectory(targetDirectory);
            var items = new List<PastePlanItem>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in NormalizeSources(sources))
            {
                var entry = Store.GetEntry(source) ?? throw new FileOperationException($"'{Path.GetFileName(source)}'이(가) 더 이상 없습니다: {source}");
                if (entry.IsDirectory && (FilePaths.AreSame(target, source) || FilePaths.IsInside(target, source)))
                {
                    throw new FileOperationException($"폴더를 자기 자신이나 그 하위 폴더에 붙여넣을 수 없습니다: {source}");
                }

                var sameFolder = FilePaths.AreSame(Path.GetDirectoryName(source)!, target);
                if (isMove && entry.IsDirectory && !sameFolder
                    && !string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase))
                {
                    throw new FileOperationException($"폴더는 다른 드라이브로 이동할 수 없습니다. 복사한 뒤 원본을 삭제하세요: {source}");
                }

                var destination = Path.Combine(target, Path.GetFileName(source));
                if (!sameFolder && !names.Add(Path.GetFileName(source)))
                {
                    throw new FileOperationException($"같은 이름의 항목 여러 개를 한 폴더에 붙여넣을 수 없습니다: {Path.GetFileName(source)}");
                }

                var existing = sameFolder ? null : Store.GetEntry(destination);
                var kind = sameFolder ? (isMove ? PasteItemKind.AlreadyThere : PasteItemKind.SameFolder)
                    : existing is null ? PasteItemKind.Free
                    : PasteItemKind.Conflict;
                items.Add(new PastePlanItem(source, destination, entry.IsDirectory, kind)
                {
                    CanOverwrite = kind == PasteItemKind.Conflict && !entry.IsDirectory && existing is { IsDirectory: false },
                });
            }

            return new PastePlan(target, isMove, items);
        }
    }

    /// <summary>
    /// Runs a plan with the user's choice for each conflict (keyed by source path). Overwriting backs the old file up
    /// (unless it is a secret file) and sends it to the recycle bin, so the whole paste is one undo step.
    /// </summary>
    public FileOperationResult Paste(PastePlan plan, IReadOnlyDictionary<string, ConflictChoice> choices)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(choices);
        lock (_sync)
        {
            var verb = plan.IsMove ? "이동" : "붙여넣기";
            var work = new List<(PastePlanItem Item, string Destination, bool Overwrite)>();
            foreach (var item in plan.Items)
            {
                if (item.Kind == PasteItemKind.AlreadyThere)
                {
                    continue;
                }

                // The plan may be old (a dialog was open): the source must still be what was planned.
                if (Store.GetEntry(item.Source) is not { } current)
                {
                    throw new FileOperationException($"'{Path.GetFileName(item.Source)}'이(가) 더 이상 없습니다: {item.Source}");
                }

                if (current.IsDirectory != item.IsDirectory
                    || (current.IsDirectory && (FilePaths.AreSame(plan.TargetDirectory, item.Source) || FilePaths.IsInside(plan.TargetDirectory, item.Source))))
                {
                    throw new FileOperationException($"붙여넣는 사이에 항목이 바뀌었습니다. 다시 시도하세요: {item.Source}");
                }

                var taken = Store.GetEntry(item.Destination) is not null;
                var choice = item.Kind == PasteItemKind.SameFolder ? ConflictChoice.KeepBoth
                    : taken ? choices.TryGetValue(item.Source, out var c) && item.Kind == PasteItemKind.Conflict
                        ? c
                        : throw new FileOperationException($"붙여넣는 사이에 같은 이름의 항목이 생겼습니다. 다시 시도하세요: {item.Destination}")
                    : ConflictChoice.KeepBoth;
                if (choice == ConflictChoice.Skip)
                {
                    continue;
                }

                if (choice == ConflictChoice.Overwrite && !(item.CanOverwrite && Store.GetEntry(item.Destination) is { IsDirectory: false }))
                {
                    throw new FileOperationException($"폴더는 덮어쓸 수 없습니다: {item.Destination}");
                }

                var destination = choice == ConflictChoice.KeepBoth && taken
                    ? Path.Combine(plan.TargetDirectory, FileNameRules.MakeCopyName(Path.GetFileName(item.Source), item.IsDirectory,
                        n => Store.GetEntry(Path.Combine(plan.TargetDirectory, n)) is not null || work.Any(w => FilePaths.AreSame(w.Destination, Path.Combine(plan.TargetDirectory, n)))))
                    : item.Destination;
                work.Add((item, destination, choice == ConflictChoice.Overwrite));
            }

            if (work.Count == 0)
            {
                return new FileOperationResult(verb, [], []);
            }

            // Check before changing anything: overwritten files must be recyclable and backed up.
            foreach (var (_, destination, overwrite) in work.Where(w => w.Overwrite))
            {
                if (new RecycleStep(destination).Check(_context) is { } problem)
                {
                    throw new FileOperationException(problem);
                }
            }

            var warnings = new List<string>();
            BackupOverwritten(work.Where(w => w.Overwrite).Select(w => w.Destination).ToList(), warnings);

            var description = CountDescription(verb, work.Count);
            var inverses = new List<FileStep>();
            var changes = new List<PathChange>();
            var paths = new List<string>();
            try
            {
                foreach (var (item, destination, overwrite) in work)
                {
                    if (overwrite)
                    {
                        RunStep(new RecycleStep(destination), inverses, changes, warnings);
                    }

                    if (plan.IsMove)
                    {
                        RunStep(new MoveStep(item.Source, destination), inverses, changes, warnings);
                    }
                    else
                    {
                        try
                        {
                            Store.Copy(item.Source, destination);
                        }
                        catch (PartialCopyException partial) when (Store.GetEntry(partial.Destination) is not null)
                        {
                            inverses.Insert(0, new RecycleStep(partial.Destination));
                            changes.Add(new PathChange.Created(partial.Destination));
                            throw;
                        }

                        inverses.Insert(0, new RecycleStep(destination));
                        changes.Add(new PathChange.Created(destination));
                    }

                    paths.Add(destination);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileOperationException)
            {
                var partial = RecordIfAny(description, inverses);
                var cause = ex is FileOperationException { InnerException: { } inner } ? inner : ex;
                throw new FileOperationException(FailureMessage(verb, paths.Count, work.Count, cause), partial, cause) { Changes = changes };
            }

            History.Record(new FileOperationEntry(description, inverses));
            return new FileOperationResult(description, paths, warnings) { Changes = changes };
        }
    }

    private void RunStep(FileStep step, List<FileStep> inverses, List<PathChange> changes, List<string> warnings)
    {
        if (step.Check(_context) is { } problem)
        {
            throw new FileOperationException(problem);
        }

        var inverse = step.Apply(_context);
        inverses.Insert(0, inverse);
        changes.Add(ChangeOf(step));
        if (inverse is RestoreStep { Ticket.CanRestore: false } restore)
        {
            warnings.Add($"'{Path.GetFileName(restore.Ticket.OriginalPath)}'은(는) 휴지통에서 위치를 찾지 못해 DotDeck에서 되돌릴 수 없습니다. 백업 목록에서 복원하세요.");
        }
    }

    /// <summary>SPEC 6: back up files before they are replaced. Secret files are not copied into backups.</summary>
    private void BackupOverwritten(IReadOnlyList<string> paths, List<string> warnings)
    {
        if (_backups is null || paths.Count == 0)
        {
            return;
        }

        var sources = new List<BackupSource>();
        foreach (var path in paths)
        {
            if (SecretFilePolicy.IsSecret(Path.GetFileName(path)))
            {
                warnings.Add($"비밀 파일은 백업하지 않고 휴지통에만 보관했습니다: {path}");
                continue;
            }

            try
            {
                sources.Add(new BackupSource(path, Store.ReadAllBytes(path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new FileOperationException($"덮어쓸 파일을 백업하지 못해 붙여넣지 않았습니다: {path}\n{ex.Message}", innerException: ex);
            }
        }

        if (sources.Count == 0)
        {
            return;
        }

        try
        {
            _backups.Create(sources, "붙여넣기 덮어쓰기");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new FileOperationException($"덮어쓸 파일을 백업하지 못해 붙여넣지 않았습니다.\n{ex.Message}", innerException: ex);
        }
    }

    private void ThrowIfAnyCheckFails(IReadOnlyList<FileStep> steps)
    {
        var problems = new List<string>();

        // Paths an earlier step of this list frees (e.g. undo of an overwrite: recycle the copy, then restore the
        // original there). Steps that put something there are checked again right before they run (Apply).
        var freed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in steps)
        {
            var target = step switch { MoveStep m => m.To, RestoreStep r => r.Ticket.OriginalPath, _ => null };
            var skip = target is not null && freed.Contains(FilePaths.Normalize(target));
            switch (step)
            {
                case RecycleStep recycle:
                    freed.Add(FilePaths.Normalize(recycle.Path));
                    break;
                case MoveStep move:
                    freed.Add(FilePaths.Normalize(move.From));
                    break;
            }

            if (skip)
            {
                continue;
            }

            try
            {
                if (step.Check(_context) is { } problem)
                {
                    problems.Add(problem);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add(ex.Message);
            }
        }

        if (problems.Count > 0)
        {
            throw new FileOperationException(string.Join("\n", problems.Distinct().Take(10)));
        }
    }

    private bool RecordIfAny(string description, List<FileStep> undoSteps)
    {
        if (undoSteps.Count == 0)
        {
            return false;
        }

        History.Record(new FileOperationEntry(description, undoSteps));
        return true;
    }

    /// <summary>Full paths, without duplicates, drive roots, or items whose folder is also in the list.</summary>
    private static List<string> NormalizeSources(IReadOnlyList<string> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var normalized = sources.Select(FilePaths.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var path in normalized)
        {
            if (Path.GetDirectoryName(path) is null)
            {
                throw new FileOperationException($"드라이브 루트에는 이 작업을 할 수 없습니다: {path}");
            }
        }

        return normalized.Where(p => !normalized.Any(other => FilePaths.IsInside(p, other))).ToList();
    }

    private string RequireDirectory(string directory)
    {
        var full = FilePaths.Normalize(directory);
        return Store.DirectoryExists(full) ? full : throw new FileOperationException($"폴더가 없습니다: {full}");
    }

    private static void ThrowIfInvalidName(string name)
    {
        if (FileNameRules.Validate(name) is { } problem)
        {
            throw new FileOperationException(problem);
        }
    }

    private static string CountDescription(string verb, int count) => count == 1 ? verb : $"{verb} ({count}개)";

    private static string FailureMessage(string what, int done, int total, Exception error)
    {
        var progress = done > 0 ? $"{total}개 중 {done}개를 처리한 뒤 " : string.Empty;
        return $"{what} 작업이 {progress}실패했습니다.\n{error.Message}";
    }
}
