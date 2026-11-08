using DotDeck.Core.Explorer;
using DotDeck.Core.Files;

namespace DotDeck.Core.Tests.Security;

public sealed class SecretFileResolverTests
{
    [Fact]
    public void Secret_name_is_secret_without_touching_links()
    {
        var store = new LinkStore();
        Assert.True(SecretFileResolver.IsSecretFile(store, @"C:\home\.ssh\id_rsa"));
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public void Plain_file_without_links_is_not_secret()
    {
        Assert.False(SecretFileResolver.IsSecretFile(new LinkStore(), @"C:\home\notes.txt"));
    }

    [Fact]
    public void File_link_to_secret_is_secret()
    {
        var store = new LinkStore { [@"C:\home\notes.txt"] = @"C:\keys\id_ed25519" };
        Assert.True(SecretFileResolver.IsSecretFile(store, @"C:\home\notes.txt"));
    }

    [Fact]
    public void Link_chain_is_followed_to_the_end()
    {
        var store = new LinkStore
        {
            [@"C:\home\a.txt"] = @"C:\home\b.txt",
            [@"C:\home\b.txt"] = @"C:\vault\c.txt",
            [@"C:\vault\c.txt"] = @"C:\vault\server.pem",
        };
        Assert.True(SecretFileResolver.IsSecretFile(store, @"C:\home\a.txt"));
    }

    [Fact]
    public void Intermediate_secret_name_in_chain_is_secret()
    {
        var store = new LinkStore
        {
            [@"C:\home\a.txt"] = @"C:\home\.env",
            [@"C:\home\.env"] = @"C:\blob\data.bin",
        };
        Assert.True(SecretFileResolver.IsSecretFile(store, @"C:\home\a.txt"));
    }

    [Fact]
    public void Folder_link_or_junction_in_the_middle_of_the_path_is_resolved()
    {
        // C:\home\j is a junction to C:\real; C:\real\note.txt links (relative) to ..\vault\cert.key
        var store = new LinkStore
        {
            [@"C:\home\j"] = @"C:\real",
            [@"C:\real\note.txt"] = @"C:\vault\cert.key",
        };
        Assert.True(SecretFileResolver.IsSecretFile(store, @"C:\home\j\note.txt"));
        Assert.False(SecretFileResolver.IsSecretFile(store, @"C:\home\j\other.txt"));
    }

    [Fact]
    public void Link_cycle_is_treated_as_secret()
    {
        var store = new LinkStore
        {
            [@"C:\home\a.txt"] = @"C:\home\b.txt",
            [@"C:\home\b.txt"] = @"C:\home\a.txt",
        };
        Assert.True(SecretFileResolver.IsSecretFile(store, @"C:\home\a.txt"));
    }

    [Fact]
    public void Real_junction_folder_is_resolved()
    {
        using var temp = new Support.TempDirectory();
        var real = temp.Combine("real");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "x.txt"), "x");
        var junction = temp.Combine("j");
        var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/J", junction, real },
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        mklink.WaitForExit();
        if (!Directory.Exists(junction)) Assert.Skip("Junction could not be created.");

        Assert.Equal(Path.GetFullPath(real), PhysicalFileStore.Instance.GetLinkTarget(junction)?.TrimEnd('\\'), ignoreCase: true);
        Assert.Null(PhysicalFileStore.Instance.GetLinkTarget(Path.Combine(real, "x.txt")));
        Assert.False(SecretFileResolver.IsSecretFile(PhysicalFileStore.Instance, Path.Combine(junction, "x.txt")));
        Directory.Delete(junction);
    }

    private sealed class LinkStore : Dictionary<string, string>, IFileStore
    {
        public LinkStore() : base(StringComparer.OrdinalIgnoreCase) { }
        public int Calls { get; private set; }
        public string? GetLinkTarget(string path)
        {
            Calls++;
            return TryGetValue(path.TrimEnd('\\'), out var target) ? target : null;
        }
        public string? ResolveLinkTarget(string path) => throw new NotSupportedException();
        public bool FileExists(string path) => throw new NotSupportedException();
        public bool DirectoryExists(string path) => throw new NotSupportedException();
        public FileSystemEntry? GetEntry(string path) => throw new NotSupportedException();
        public IReadOnlyList<FileSystemEntry> GetChildren(string directory) => throw new NotSupportedException();
        public byte[] ReadAllBytes(string path) => throw new NotSupportedException();
        public byte[] ReadHead(string path, int maxBytes) => throw new NotSupportedException();
        public void WriteAllBytes(string path, byte[] bytes) => throw new NotSupportedException();
        public void CreateDirectory(string path) => throw new NotSupportedException();
        public void CreateFile(string path) => throw new NotSupportedException();
        public void Move(string source, string destination, bool overwrite = false) => throw new NotSupportedException();
        public void Copy(string source, string destination) => throw new NotSupportedException();
        public void ReplaceFile(string source, string destination) => throw new NotSupportedException();
        public void DeleteDirectory(string path) => throw new NotSupportedException();
        public void DeleteFile(string path) => throw new NotSupportedException();
        public IDisposable AcquireLock(string lockPath, TimeSpan timeout) => throw new NotSupportedException();
    }
}
