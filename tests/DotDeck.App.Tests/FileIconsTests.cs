using System.IO;
using System.Windows;
using System.Windows.Media;
using DotDeck.App.Converters;
using DotDeck.App.Themes;
using DotDeck.App.ViewModels.Editor;
using DotDeck.Core.Documents;
using DotDeck.Core.Explorer;
using DotDeck.Core.Files;
using DotDeck.Core.Logging;
using DotDeck.Core.Paths;

namespace DotDeck.App.Tests;

/// <summary>Tree and tab file icons: key resolution with the shipped mapping, image loading, dirty badges.</summary>
public sealed class FileIconsTests
{
    private static Task RunSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    [Theory]
    [InlineData("notes.md", false, false, "markdown")]
    [InlineData("settings.json", false, false, "json")]
    [InlineData("config.toml", false, false, "toml")]
    [InlineData("unknown.zzz", false, false, "file")]
    [InlineData("src", true, false, "folder-src")]
    [InlineData("src", true, true, "folder-src-open")]
    [InlineData("whatever", true, true, "folder-open")]
    public void Shipped_mapping_gives_material_keys(string name, bool folder, bool expanded, string expected) =>
        Assert.Equal(expected, FileIcons.KeyFor(name, null, folder, expanded));

    [Fact]
    public Task Images_are_frozen_cached_and_fall_back_to_the_generic_icon() => RunSta(() =>
    {
        var markdown = FileIcons.Image("markdown");
        Assert.NotNull(markdown);
        Assert.True(markdown!.IsFrozen);
        Assert.Same(markdown, FileIcons.Image("markdown"));
        Assert.Null(FileIcons.Image("no-such-icon"));
        // A key that cannot be drawn falls back to the generic icon of its kind.
        Assert.NotNull(FileIcons.Image("file"));
        Assert.Same(FileIcons.Image("file"), FileIcons.ImageOrDefault("no-such-icon", isFolder: false, isExpanded: false));
        Assert.NotNull(FileIcons.Image("folder-open"));
        Assert.Same(FileIcons.Image("folder-open"), FileIcons.ImageOrDefault("no-such-icon", isFolder: true, isExpanded: true));
    });

    [Theory]
    [InlineData("D:/work/sub/../a.md")]
    [InlineData(@"d:\WORK\a.md")]
    [InlineData(@"\\?\D:\work\a.md")]
    public void Dirty_paths_match_whatever_their_spelling(string spelling)
    {
        var path = @"D:\work\a.md";
        FileIcons.SetDirty(path, true);
        try
        {
            Assert.True(FileIcons.IsDirty(spelling));
        }
        finally
        {
            FileIcons.SetDirty(spelling, false);
        }

        Assert.False(FileIcons.IsDirty(path));
        Assert.False(FileIcons.IsDirty("a.md")); // relative: never dirty
    }

    [Fact]
    public Task Converter_handles_tree_nodes_and_tab_paths() => RunSta(() =>
    {
        var converter = new FileIconConverter();
        var folder = new NodeInfo { Id = "src", Name = "src", Kind = NodeKind.Folder, FullPath = @"D:\work\src" };
        var file = @"D:\work\notes.md";

        Assert.Same(FileIcons.Image("folder-src-open"), converter.Convert([folder, true, 0], typeof(ImageSource), null!, null!));
        Assert.Same(FileIcons.Image("markdown"), converter.Convert([file, 0], typeof(ImageSource), null!, null!));
        Assert.Null(converter.Convert([DependencyProperty.UnsetValue, 0], typeof(ImageSource), null!, null!));
    });

    [Fact]
    public void Dirty_badge_follows_the_set_and_bumps_the_version()
    {
        var path = @"D:\work\" + Guid.NewGuid().ToString("N") + ".md";
        var converter = new DirtyFileToVisibleConverter();
        var version = FileIcons.DirtyVersion;

        FileIcons.SetDirty(path, true);
        Assert.Equal(Visibility.Visible, converter.Convert([path.ToUpperInvariant(), 0], typeof(Visibility), null!, null!));
        Assert.True(FileIcons.DirtyVersion > version);

        FileIcons.SetDirty(path, false);
        Assert.Equal(Visibility.Collapsed, converter.Convert([path, 0], typeof(Visibility), null!, null!));
        Assert.Equal(Visibility.Collapsed, converter.Convert([null!, 0], typeof(Visibility), null!, null!));
    }

    [Fact]
    public async Task Buffer_manager_reports_dirty_changes_and_closing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotdeck-icons-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "a.md");
            await File.WriteAllTextAsync(path, "x", TestContext.Current.CancellationToken);
            var manager = new DocumentBufferManager(new TextFileReader(PhysicalFileStore.Instance));
            var events = new List<(string Path, bool Dirty)>();
            manager.DirtyChanged += (p, d) => events.Add((p, d));

            var buffer = await manager.AcquireAsync(path);
            buffer.Document.Insert(0, "y");
            manager.Release(buffer);

            Assert.Equal([(buffer.Path, true), (buffer.Path, false)], events);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class Log : IAppLog
    {
        public List<string> Warnings { get; } = [];

        public event EventHandler<AppLogEntry>? EntryWritten
        {
            add { }
            remove { }
        }

        public void Write(AppLogLevel level, string message, Exception? exception = null)
        {
            if (level == AppLogLevel.Warning)
            {
                Warnings.Add(message);
            }
        }
    }

    [Fact]
    public void Dotdeck_mapping_loads_cleanly_and_gives_tool_icons_only_inside_tool_folders()
    {
        var root = Path.Combine(Path.GetTempPath(), "dotdeck-icons-" + Guid.NewGuid().ToString("N"));
        var log = new Log();
        try
        {
            var home = Path.Combine(root, "home");
            FileIcons.Configure(new AppPaths(home, HomeSource.CommandLine, Path.Combine(root, "roaming"), Path.Combine(root, "local")), log);
            Assert.Empty(log.Warnings); // every key in dotdeck-icons.json exists and every rule is valid

            // Distinctive names: everywhere.
            Assert.Equal("dotdeck-claude", FileIcons.KeyFor("CLAUDE.md", @"D:\work\CLAUDE.md", false, false));
            Assert.Equal("dotdeck-shared", FileIcons.KeyFor("AGENTS.md", @"D:\work\AGENTS.md", false, false));
            Assert.Equal("dotdeck-agy", FileIcons.KeyFor("GEMINI.md", @"D:\work\GEMINI.md", false, false));
            Assert.Equal("dotdeck-folder-codex-open", FileIcons.KeyFor(".codex", @"D:\work\.codex", true, true));

            // Common names: only inside the tool's folder (workspace or built-in home).
            Assert.Equal("dotdeck-folder-claude", FileIcons.KeyFor("agents", @"D:\work\.claude\agents", true, false));
            Assert.Equal("folder-robot", FileIcons.KeyFor("agents", @"D:\work\agents", true, false));
            Assert.Equal("dotdeck-claude", FileIcons.KeyFor("settings.json", Path.Combine(home, ".claude", "settings.json"), false, false));
            Assert.Equal("json", FileIcons.KeyFor("settings.json", @"D:\work\.vscode\settings.json", false, false));
            Assert.Equal("dotdeck-codex", FileIcons.KeyFor("config.toml", Path.Combine(home, ".codex", "config.toml"), false, false));
            Assert.Equal("toml", FileIcons.KeyFor("config.toml", @"D:\work\config.toml", false, false));
            Assert.Equal("dotdeck-agy", FileIcons.KeyFor("mcp_config.json", Path.Combine(home, ".gemini", "antigravity", "mcp_config.json"), false, false));
            Assert.Equal("dotdeck-folder-agy", FileIcons.KeyFor("config", Path.Combine(home, ".gemini", "config"), true, false));
            Assert.Equal("folder-config", FileIcons.KeyFor("config", @"D:\work\config", true, false));
            // hooks: only directly below the Claude folder.
            Assert.Equal("folder-hook", FileIcons.KeyFor("hooks", @"D:\work\.claude\agents\hooks", true, false));
        }
        finally
        {
            FileIcons.Configure(new AppPaths(Path.Combine(root, "none"), HomeSource.CommandLine, Path.Combine(root, "none"), Path.Combine(root, "none")), new Log());
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void User_associations_override_and_reload_and_bad_entries_only_warn()
    {
        var root = Path.Combine(Path.GetTempPath(), "dotdeck-icons-" + Guid.NewGuid().ToString("N"));
        var log = new Log();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "home"), HomeSource.CommandLine, Path.Combine(root, "roaming"), Path.Combine(root, "local"));
            Directory.CreateDirectory(Path.GetDirectoryName(paths.IconAssociationsFile)!);
            File.WriteAllText(paths.IconAssociationsFile, """
                { "fileExtensions": { "zzq": "markdown" }, "fileNames": { "CLAUDE.md": "toml", "x.zzq": "custom:mine", "y.zzq": "nope" } }
                """);
            FileIcons.Configure(paths, log);

            Assert.Equal("markdown", FileIcons.KeyFor("a.zzq", null, false, false));
            Assert.Equal("toml", FileIcons.KeyFor("CLAUDE.md", null, false, false)); // user layer wins over DotDeck's
            Assert.Equal("markdown", FileIcons.KeyFor("x.zzq", null, false, false)); // custom: is reserved, falls through
            Assert.Equal(2, log.Warnings.Count);

            var version = FileIcons.Version;
            Assert.False(FileIcons.Reload()); // unchanged file: nothing to do

            // Locked while an editor saves: the current mapping stays.
            using (new FileStream(paths.IconAssociationsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.False(FileIcons.Reload());
                Assert.Equal("toml", FileIcons.KeyFor("CLAUDE.md", null, false, false));
            }

            File.WriteAllText(paths.IconAssociationsFile, "{ not json");
            Assert.True(FileIcons.Reload());
            Assert.True(FileIcons.Version > version);
            Assert.Equal("dotdeck-claude", FileIcons.KeyFor("CLAUDE.md", null, false, false)); // broken file: ignored
            Assert.Contains(log.Warnings, w => w.Contains("읽지 못해", StringComparison.Ordinal));
        }
        finally
        {
            FileIcons.Configure(new AppPaths(Path.Combine(root, "none"), HomeSource.CommandLine, Path.Combine(root, "none"), Path.Combine(root, "none")), new Log());
            Directory.Delete(root, recursive: true);
        }
    }
}
