using System.IO;
using System.Windows;
using System.Windows.Media;
using DotDeck.App.Converters;
using DotDeck.App.Themes;
using DotDeck.App.ViewModels.Editor;
using DotDeck.Core.Documents;
using DotDeck.Core.Explorer;
using DotDeck.Core.Files;

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
}
