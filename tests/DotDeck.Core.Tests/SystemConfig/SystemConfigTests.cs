using DotDeck.Core.Files;
using DotDeck.Core.Security;
using DotDeck.Core.SystemConfig;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.SystemConfig;

public sealed class SystemConfigTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 8, 9, 0, 0, TimeSpan.Zero));

    public void Dispose() => _temp.Dispose();

    private FileEnvironmentStore Env(bool machine = true) => new(PhysicalFileStore.Instance, _temp.Combine("env.json"), machine);

    [Fact]
    public void Compute_finds_added_changed_and_removed_variables()
    {
        var before = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["A"] = "1", ["B"] = "2", ["C"] = "3" };

        var changes = EnvironmentChanges.Compute(before, [new("a", "1"), new("B", "20"), new("D", "4"), new(" ", "x")]);

        Assert.Equal(
            [new EnvironmentChange("B", "2", "20"), new EnvironmentChange("C", "3", null), new EnvironmentChange("D", null, "4")],
            changes);
    }

    [Fact]
    public void Apply_records_history_with_secrets_masked_and_revert_skips_them()
    {
        var env = Env();
        env.Set(EnvironmentScope.User, "API_TOKEN", "old-secret");
        env.Set(EnvironmentScope.User, "EDITOR", "vim");
        var backups = _temp.Combine("backups");
        var changes = EnvironmentChanges.Compute(env.Read(EnvironmentScope.User), [new("API_TOKEN", "new-secret"), new("EDITOR", "code")]);

        EnvironmentChanges.Apply(env, EnvironmentScope.User, changes, PhysicalFileStore.Instance, backups, _time);

        Assert.Equal("code", env.Read(EnvironmentScope.User)["EDITOR"]);
        var file = Directory.GetFiles(backups).Single();
        var text = File.ReadAllText(file);
        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
        Assert.Contains(SecretMasker.Mask, text, StringComparison.Ordinal);

        var record = EnvironmentChanges.History(PhysicalFileStore.Instance, backups).Single();
        var undo = EnvironmentChanges.Revert(record, out var skipped);
        Assert.Equal(1, skipped);
        Assert.Equal([new EnvironmentChange("EDITOR", "code", "vim")], undo);
    }

    [Fact]
    public void Preview_masks_secret_values()
    {
        var (before, after) = EnvironmentChanges.PreviewText([new EnvironmentChange("MY_KEY", "abc", "def"), new EnvironmentChange("X", null, "1")]);

        Assert.DoesNotContain("abc", before, StringComparison.Ordinal);
        Assert.DoesNotContain("def", after, StringComparison.Ordinal);
        Assert.Contains("X=1", after, StringComparison.Ordinal);
    }

    [Fact]
    public void Machine_scope_is_read_only_without_elevation()
    {
        var env = Env(machine: false);

        Assert.False(env.CanWrite(EnvironmentScope.Machine));
        Assert.Throws<UnauthorizedAccessException>(() => env.Set(EnvironmentScope.Machine, "A", "1"));
    }

    [Fact]
    public void Path_analysis_reports_missing_and_duplicate_entries()
    {
        var entries = PathList.Split(@"C:\a; C:\b\ ;;%NOPE%\x;C:\A");
        var existing = new HashSet<string>([@"C:\a", @"C:\b\"], StringComparer.OrdinalIgnoreCase);

        var issues = PathList.Analyze(entries, existing.Contains, s => s);

        Assert.Equal(4, entries.Count);
        Assert.Equal([new PathEntryIssue(3, "1번 항목과 중복됩니다.")], issues);
        Assert.Equal(@"C:\a;C:\b\;%NOPE%\x;C:\A", PathList.Join(entries));
    }

    [Fact]
    public void Important_files_add_remove_and_refuse_secrets()
    {
        var list = new ImportantFiles(PhysicalFileStore.Instance, _temp.Combine("cfg", "important.json"));
        string[] defaults = [@"C:\h\.gitconfig", @"C:\h\.npmrc"];

        Assert.NotNull(list.Add(@"C:\h\.ssh\id_rsa"));
        Assert.NotNull(list.Add("relative.txt"));
        Assert.Null(list.Add(@"C:\h\my.ini"));
        list.Remove(@"C:\h\.npmrc");
        Assert.Equal([@"C:\h\.gitconfig", @"C:\h\my.ini"], list.List(defaults));

        // A default that was removed, added back and removed again stays hidden.
        Assert.Null(list.Add(@"C:\h\.npmrc"));
        Assert.Contains(@"C:\h\.npmrc", list.List(defaults));
        list.Remove(@"C:\h\.npmrc");
        Assert.DoesNotContain(@"C:\h\.npmrc", list.List(defaults));
    }

    [Fact]
    public void Conflicts_detect_values_changed_since_they_were_read()
    {
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["A"] = "changed", ["B"] = "2" };

        var conflicts = EnvironmentChanges.Conflicts(current, [new("A", "1", "x"), new("B", "2", "y"), new("C", null, "z")]);

        Assert.Equal(["A"], conflicts);
    }

    [Fact]
    public void Partial_apply_is_still_recorded()
    {
        var env = new FailingEnvironment();
        var backups = _temp.Combine("backups");

        Assert.Throws<IOException>(() => EnvironmentChanges.Apply(env, EnvironmentScope.User,
            [new("A", null, "1"), new("FAIL", null, "2")], PhysicalFileStore.Instance, backups, _time));

        var record = EnvironmentChanges.History(PhysicalFileStore.Instance, backups).Single();
        Assert.Equal([new EnvironmentChange("A", null, "1")], record.Changes);
    }

    private sealed class FailingEnvironment : IEnvironmentStore
    {
        public bool CanWrite(EnvironmentScope scope) => true;

        public IReadOnlyDictionary<string, string> Read(EnvironmentScope scope) => new Dictionary<string, string>();

        public void Set(EnvironmentScope scope, string name, string? value)
        {
            if (name == "FAIL")
            {
                throw new IOException("boom");
            }
        }

        public void Broadcast()
        {
        }
    }
}
