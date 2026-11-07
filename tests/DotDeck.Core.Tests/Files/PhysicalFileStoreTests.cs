using DotDeck.Core.Files;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.Files;

public sealed class PhysicalFileStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly PhysicalFileStore _store = PhysicalFileStore.Instance;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void GetChildren_lists_files_and_folders_without_recursion()
    {
        Directory.CreateDirectory(_temp.Combine("sub", "deep"));
        File.WriteAllText(_temp.Combine("a.md"), "hello");

        var children = _store.GetChildren(_temp.Path).OrderBy(c => c.Name, StringComparer.Ordinal).ToList();

        Assert.Equal(["a.md", "sub"], children.Select(c => c.Name));
        Assert.False(children[0].IsDirectory);
        Assert.Equal(5, children[0].Length);
        Assert.True(children[1].IsDirectory);
    }

    [Fact]
    public void ReadHead_reads_at_most_requested_bytes()
    {
        File.WriteAllText(_temp.Combine("h.txt"), "abcdef");

        Assert.Equal("abc"u8.ToArray(), _store.ReadHead(_temp.Combine("h.txt"), 3));
        Assert.Equal("abcdef"u8.ToArray(), _store.ReadHead(_temp.Combine("h.txt"), 100));
    }

    [Fact]
    public void GetEntry_returns_null_for_missing_path()
    {
        Assert.Null(_store.GetEntry(_temp.Combine("missing")));
    }

    [Fact]
    public void Move_does_not_overwrite_existing_file_by_default()
    {
        File.WriteAllText(_temp.Combine("a.txt"), "a");
        File.WriteAllText(_temp.Combine("b.txt"), "b");

        Assert.ThrowsAny<IOException>(() => _store.Move(_temp.Combine("a.txt"), _temp.Combine("b.txt")));
        Assert.Equal("b", File.ReadAllText(_temp.Combine("b.txt")));
    }

    [Fact]
    public void Move_directory_refuses_existing_destination()
    {
        Directory.CreateDirectory(_temp.Combine("x"));
        Directory.CreateDirectory(_temp.Combine("y"));

        Assert.ThrowsAny<IOException>(() => _store.Move(_temp.Combine("x"), _temp.Combine("y")));
    }

    [Fact]
    public void Move_directory_allows_case_only_rename()
    {
        Directory.CreateDirectory(_temp.Combine("folder"));

        _store.Move(_temp.Combine("folder"), _temp.Combine("Folder"));

        Assert.Equal("Folder", new DirectoryInfo(_temp.Path).GetDirectories().Single().Name);
    }

    [Fact]
    public void Copy_directory_recursively_and_refuses_existing_destination()
    {
        Directory.CreateDirectory(_temp.Combine("src", "inner"));
        File.WriteAllText(_temp.Combine("src", "inner", "f.txt"), "x");

        _store.Copy(_temp.Combine("src"), _temp.Combine("dst"));

        Assert.Equal("x", File.ReadAllText(_temp.Combine("dst", "inner", "f.txt")));
        Assert.ThrowsAny<IOException>(() => _store.Copy(_temp.Combine("src"), _temp.Combine("dst")));
    }

    [Fact]
    public void Copy_directory_into_itself_is_rejected()
    {
        Directory.CreateDirectory(_temp.Combine("src"));

        Assert.ThrowsAny<IOException>(() => _store.Copy(_temp.Combine("src"), _temp.Combine("src", "child")));
    }

    [Fact]
    public void Copy_into_itself_through_a_junction_terminates_without_nesting()
    {
        Directory.CreateDirectory(_temp.Combine("src"));
        File.WriteAllText(_temp.Combine("src", "f.txt"), "x");
        var link = _temp.Combine("link");
        if (!TryCreateJunction(link, _temp.Combine("src")))
        {
            Assert.Skip("Junction creation is not available in this environment.");
        }

        // Destination lies inside the source via the junction; the string prefix check cannot see it.
        _store.Copy(_temp.Combine("src"), Path.Combine(link, "out"));

        Assert.Equal("x", File.ReadAllText(_temp.Combine("src", "out", "f.txt")));
        Assert.False(Directory.Exists(_temp.Combine("src", "out", "out")));
    }

    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                ArgumentList = { "/c", "mklink", "/J", link, target },
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            process!.WaitForExit(10_000);
            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [Fact]
    public void AcquireLock_is_exclusive_until_disposed()
    {
        var lockPath = _temp.Combine("x.lock");

        using (_store.AcquireLock(lockPath, TimeSpan.FromSeconds(1)))
        {
            Assert.ThrowsAny<IOException>(() => _store.AcquireLock(lockPath, TimeSpan.FromMilliseconds(100)));
        }

        using var again = _store.AcquireLock(lockPath, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ReadAllText_rejects_invalid_utf8()
    {
        var path = _temp.Combine("bad.txt");
        File.WriteAllBytes(path, [0x61, 0xC3, 0x28]);

        Assert.Throws<System.Text.DecoderFallbackException>(() => _store.ReadAllText(path));
    }

    [Fact]
    public void WriteAllTextAtomic_writes_utf8_without_bom_and_leaves_no_temp_files()
    {
        var path = _temp.Combine("data", "projects.json");

        _store.WriteAllTextAtomic(path, "{\"a\":\"한글\"}");
        _store.WriteAllTextAtomic(path, "{\"a\":\"두번째\"}");

        var bytes = File.ReadAllBytes(path);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal("{\"a\":\"두번째\"}", _store.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_temp.Combine("data")));
    }

    [Fact]
    public void ReadAllText_strips_utf8_bom()
    {
        var path = _temp.Combine("bom.txt");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i']);

        Assert.Equal("hi", _store.ReadAllText(path));
    }
}
