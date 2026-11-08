using DotDeck.Core.Files;

namespace DotDeck.Core.FileOperations;

/// <summary>Dependencies a step runs against.</summary>
internal sealed record FileStepContext(IFileStore Store, IRecycleBin RecycleBin);

/// <summary>
/// One primitive, reversible change. Every operation (and every undo/redo) is a list of steps; applying a step
/// returns its inverse, so undo and redo are the same mechanism. <see cref="Check"/> verifies preconditions so
/// a step never overwrites anything that appeared since it was recorded.
/// </summary>
internal abstract record FileStep
{
    /// <summary>Returns why the step cannot run now, or null.</summary>
    public abstract string? Check(FileStepContext context);

    /// <summary>Performs the step and returns the step that reverses it.</summary>
    public abstract FileStep Apply(FileStepContext context);

    /// <summary>Path that exists after the step ran (for selecting it in the tree), if any.</summary>
    public abstract string? ResultPath { get; }

    protected static string NameOf(string path) => Path.GetFileName(path) is { Length: > 0 } name ? name : path;
}

/// <summary>Move or rename (never overwrites; a case-only rename of the same item is allowed).</summary>
internal sealed record MoveStep(string From, string To) : FileStep
{
    public override string? ResultPath => To;

    public override string? Check(FileStepContext context)
    {
        var store = context.Store;
        if (store.GetEntry(From) is null)
        {
            return $"'{NameOf(From)}'이(가) 더 이상 없습니다: {From}";
        }

        if (store.GetEntry(To) is not null && !FilePaths.AreSame(From, To))
        {
            return $"같은 이름의 항목이 이미 있습니다: {To}";
        }

        if (Path.GetDirectoryName(To) is { } parent && !store.DirectoryExists(parent))
        {
            return $"대상 폴더가 없습니다: {parent}";
        }

        return FilePaths.IsInside(To, From) ? "폴더를 자기 자신 안으로 옮길 수 없습니다." : null;
    }

    public override FileStep Apply(FileStepContext context)
    {
        context.Store.Move(From, To);
        return new MoveStep(To, From);
    }
}

/// <summary>Sends an item to the recycle bin.</summary>
internal sealed record RecycleStep(string Path) : FileStep
{
    public override string? ResultPath => null;

    public override string? Check(FileStepContext context)
    {
        if (context.Store.GetEntry(Path) is null)
        {
            return $"'{NameOf(Path)}'이(가) 더 이상 없습니다: {Path}";
        }

        return context.RecycleBin.CanRecycle(Path)
            ? null
            : $"휴지통을 쓸 수 없는 위치라 영구 삭제가 됩니다. DotDeck은 영구 삭제하지 않습니다: {Path}";
    }

    public override FileStep Apply(FileStepContext context) => new RestoreStep(context.RecycleBin.Recycle(Path));
}

/// <summary>Puts a recycled item back where it was.</summary>
internal sealed record RestoreStep(RecycleTicket Ticket) : FileStep
{
    public override string? ResultPath => Ticket.OriginalPath;

    public override string? Check(FileStepContext context)
    {
        var original = Ticket.OriginalPath;
        if (!Ticket.CanRestore)
        {
            return $"'{NameOf(original)}'의 휴지통 위치를 찾지 못했습니다. Windows 휴지통에서 직접 복원하세요.";
        }

        if (!context.RecycleBin.Contains(Ticket))
        {
            return $"'{NameOf(original)}'이(가) 휴지통에 없습니다(비웠거나 이미 복원됨).";
        }

        if (context.Store.GetEntry(original) is not null)
        {
            return $"같은 이름의 항목이 이미 있어 복원하지 않습니다: {original}";
        }

        return System.IO.Path.GetDirectoryName(original) is { } parent && !context.Store.DirectoryExists(parent)
            ? $"원래 폴더가 없어 복원할 수 없습니다: {parent}"
            : null;
    }

    public override FileStep Apply(FileStepContext context)
    {
        context.RecycleBin.Restore(Ticket);
        return new RecycleStep(Ticket.OriginalPath);
    }
}

internal static class FilePaths
{
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static bool AreSame(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>True if <paramref name="path"/> is strictly below <paramref name="folder"/>.</summary>
    public static bool IsInside(string path, string folder) =>
        Normalize(path).StartsWith(Normalize(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
