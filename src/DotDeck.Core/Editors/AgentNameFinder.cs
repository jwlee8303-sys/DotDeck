using System.Text;
using DotDeck.Core.Files;

namespace DotDeck.Core.Editors;

/// <param name="Name">frontmatter <c>name</c>, or the file name without <c>.md</c>.</param>
/// <param name="Source">Korean label of where it was found ("사용자" or "프로젝트").</param>
public sealed record AgentNameInfo(string Name, string Source, string Path);

/// <summary>
/// Subagent names for <c>Agent(이름)</c> rules: <c>~/.claude/agents/**/*.md</c> (user, available everywhere) and, for
/// project and local settings, <c>&lt;project&gt;/.claude/agents/**/*.md</c>. Read-only, size- and count-limited.
/// </summary>
public sealed class AgentNameFinder(IFileStore store, string home)
{
    public const int MaxFiles = 500;
    public const int MaxHeadBytes = 16 * 1024;
    private const int MaxDepth = 8;

    /// <param name="projectRoot">Root of the project whose settings are edited; null for user settings.</param>
    public IReadOnlyList<AgentNameInfo> Find(string? projectRoot)
    {
        var found = new List<AgentNameInfo>();
        if (projectRoot is not null)
        {
            Collect(projectRoot, "프로젝트", found);
        }

        Collect(home, "사용자", found);

        // A project agent wins over a user agent of the same name (Claude Code's precedence).
        return found.DistinctBy(a => a.Name, StringComparer.Ordinal).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary><c>&lt;baseDir&gt;/.claude/agents</c>; a linked <c>.claude</c> or <c>agents</c> folder is not followed.</summary>
    private void Collect(string baseDir, string source, List<AgentNameInfo> found)
    {
        var claude = System.IO.Path.Combine(baseDir, ".claude");
        var directory = System.IO.Path.Combine(claude, "agents");
        try
        {
            if (store.GetEntry(claude) is not { IsDirectory: true, IsReparsePoint: false }
                || store.GetEntry(directory) is not { IsDirectory: true, IsReparsePoint: false })
            {
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((directory, 0));
        var files = 0;
        while (pending.Count > 0 && files < MaxFiles)
        {
            var (current, depth) = pending.Pop();
            IReadOnlyList<FileSystemEntry> entries;
            try
            {
                if (!store.DirectoryExists(current))
                {
                    continue;
                }

                entries = store.GetChildren(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries.Where(e => !e.IsReparsePoint))
            {
                if (entry.IsDirectory)
                {
                    if (depth < MaxDepth)
                    {
                        pending.Push((entry.FullPath, depth + 1));
                    }
                }
                else if (entry.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && files++ < MaxFiles)
                {
                    found.Add(new AgentNameInfo(NameOf(entry), source, entry.FullPath));
                }
            }
        }
    }

    private string NameOf(FileSystemEntry entry)
    {
        var fallback = System.IO.Path.GetFileNameWithoutExtension(entry.Name);
        try
        {
            var head = Encoding.UTF8.GetString(store.ReadHead(entry.FullPath, MaxHeadBytes));
            var name = FrontmatterDocument.Parse(head).Get("name")?.Trim();
            return string.IsNullOrEmpty(name) ? fallback : name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return fallback;
        }
    }
}
