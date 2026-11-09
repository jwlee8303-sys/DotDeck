using System.Text.Json.Nodes;
using DotDeck.Core.AiCli.Snapshots;
using DotDeck.Core.Documents;
using DotDeck.Core.Explorer;
using DotDeck.Core.Files;
using DotDeck.Core.Settings;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.Settings;

public sealed class PathExclusionTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private static readonly IFileStore Store = PhysicalFileStore.Instance;
    public void Dispose() => _temp.Dispose();

    [Fact]
    public void File_rules_are_exact_and_folder_rules_respect_boundaries_and_case()
    {
        var policy = new PathExclusionPolicy([new(@"C:\작업 폴더\one.md", false), new(@"C:\work\cache", true)]);
        Assert.True(policy.IsExcluded(@"c:\작업 폴더\ONE.md"));
        Assert.False(policy.IsExcluded(@"C:\작업 폴더\one.md.bak"));
        Assert.True(policy.IsExcluded(@"C:\work\cache"));
        Assert.True(policy.IsExcluded(@"C:\work\cache\not-created-yet\item.json"));
        Assert.False(policy.IsExcluded(@"C:\work\cache-other\item.json"));
        Assert.False(policy.IsExcluded(@"C:\work\cache\..\other\item.json"));
    }

    [Theory]
    [InlineData("relative.md")]
    [InlineData(@"C:\work\*.md")]
    [InlineData(@"\\.\PhysicalDrive0")]
    public void Ambiguous_paths_are_rejected(string path) => Assert.Throws<ArgumentException>(() => ExcludedPath.Normalize(path));

    [Fact]
    public void Root_quoted_extended_and_unc_paths_are_normalized()
    {
        var policy = new PathExclusionPolicy([new(@"C:\", true), new("\"\\\\server\\share\\folder\\\"", true)]);
        Assert.True(policy.IsExcluded(@"\\?\C:\deep\file.md"));
        Assert.True(policy.IsExcluded(@"\\?\UNC\server\share\folder\x.md"));
        Assert.False(policy.IsExcluded(@"\\server\share\folder-other\x.md"));
    }

    [Fact]
    public void Lists_persist_independently_preserving_unknown_fields_and_explicit_path_types()
    {
        var file = _temp.Combine("exclusions.json");
        File.WriteAllText(file, """{"future":{"enabled":true},"automaticBackup":[],"changeReview":[]}""");
        var store = new PathExclusionStore(Store, file);
        var a = _temp.Combine("backup"); var b = _temp.Combine("review.md");
        store.Add(ExclusionKind.AutomaticBackup, a, directory: true);
        store.Add(ExclusionKind.ChangeReview, b);
        store.Add(ExclusionKind.ChangeReview, b.ToUpperInvariant());
        var loaded = new PathExclusionStore(Store, file);
        Assert.Single(loaded.Get(ExclusionKind.ChangeReview));
        Assert.True(loaded.BackupPolicy.IsExcluded(Path.Combine(a, "new.md")));
        Assert.False(loaded.ReviewPolicy.IsExcluded(Path.Combine(a, "new.md")));
        Assert.True(loaded.ReviewPolicy.IsExcluded(b));
        Assert.False(loaded.BackupPolicy.IsExcluded(b));
        Assert.True(JsonNode.Parse(File.ReadAllText(file))!["future"]!["enabled"]!.GetValue<bool>());
        loaded.Replace([], []);
        Assert.False(loaded.ReviewPolicy.IsExcluded(b));
    }

    [Fact]
    public void Broken_settings_are_not_silently_replaced()
    {
        var file = _temp.Combine("exclusions.json");
        File.WriteAllText(file, "broken");
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => new PathExclusionStore(Store, file));
        Assert.Equal("broken", File.ReadAllText(file));
    }

    [Theory]
    [InlineData("broken")]
    [InlineData("{\"automaticBackup\":[null]}")]
    [InlineData("{\"changeReview\":[{\"path\":\"relative.md\"}]}")]
    public void Startup_recovery_warns_and_blocks_overwriting_invalid_settings(string original)
    {
        var file = _temp.Combine("exclusions.json");
        File.WriteAllText(file, original);
        var recovered = new PathExclusionStore(Store, file, recoverLoadFailure: true);
        Assert.NotNull(recovered.LoadWarning);
        Assert.Empty(recovered.Get(ExclusionKind.AutomaticBackup));
        Assert.Empty(recovered.Get(ExclusionKind.ChangeReview));
        Assert.Throws<IOException>(() => recovered.Replace([], []));
        Assert.Throws<IOException>(() => recovered.Add(ExclusionKind.ChangeReview, _temp.Combine("file.md")));
        Assert.Equal(original, File.ReadAllText(file));
    }

    [Fact]
    public void Independent_rules_apply_to_session_capture_review_and_force_revert()
    {
        var root = _temp.Combine("project"); Directory.CreateDirectory(root);
        var a = Path.Combine(root, "no-backup.md"); var b = Path.Combine(root, "no-review.md"); var c = Path.Combine(root, "ordinary.md");
        foreach (var file in new[] { a, b, c }) File.WriteAllText(file, "before");
        var rules = new PathExclusionStore(Store, _temp.Combine("exclusions.json"));
        rules.Add(ExclusionKind.AutomaticBackup, a); rules.Add(ExclusionKind.ChangeReview, b);
        var backups = SessionSnapshots.OpenStore(Store, _temp.Combine("session"), TimeProvider.System, null);
        backups.IsAutomaticBackupExcluded = path => rules.BackupPolicy.IsExcluded(path);
        SnapshotRoot[] roots = [new(root, ExcludedFolderPolicy.None)]; var limits = new SnapshotLimits();
        var start = SessionSnapshots.Capture(roots, [], Store, backups, limits);
        Assert.Empty(backups.ListFor(a));
        Assert.Single(backups.ListFor(b));
        foreach (var file in new[] { a, b, c }) File.WriteAllText(file, "after content");
        var summary = ChangeSummaryBuilder.Build(start, ScopeIndex.Build(roots, Store, limits, rules.ReviewPolicy.IsExcluded), backups, Store, limits, rules.ReviewPolicy.IsExcluded);
        Assert.DoesNotContain(summary.Items, i => i.Path == b);
        Assert.Contains(summary.Items, i => i.Path == a && i.PreviousFile is null);
        var item = Assert.Single(summary.Items, i => i.Path == c);
        rules.Add(ExclusionKind.ChangeReview, c);
        var planner = new RevertPlanner(Store, new SafeWriter(Store, backups), new NoRecycle(), limits, roots, path => rules.ReviewPolicy.IsExcluded(path));
        Assert.Equal(RevertStatus.NotRevertible, planner.Revert(item, force: true).Status);
        Assert.Equal("after content", File.ReadAllText(c));
    }

    [Fact]
    public void A_file_rule_does_not_prune_a_directory_that_later_reuses_the_path()
    {
        var root = _temp.Combine("project"); var reused = Path.Combine(root, "one.md");
        Directory.CreateDirectory(reused); File.WriteAllText(Path.Combine(reused, "child.md"), "document");
        var policy = new PathExclusionPolicy([new(reused, false)]);
        Assert.False(policy.IsDirectoryExcluded(reused));
        var index = ScopeIndex.Build([new(root, ExcludedFolderPolicy.None)], Store, new SnapshotLimits(), policy.IsExcluded, policy.IsDirectoryExcluded);
        Assert.Contains(Path.Combine(reused, "child.md"), index.Files.Keys);
        var folders = new PathExclusionPolicy([new(reused, true)]);
        Assert.Empty(ScopeIndex.Build([new(root, ExcludedFolderPolicy.None)], Store, new SnapshotLimits(), folders.IsExcluded, folders.IsDirectoryExcluded).Files);
    }

    [Fact]
    public void Excluded_files_still_save_but_create_no_automatic_backups()
    {
        var path = _temp.Combine("file.md"); File.WriteAllText(path, "before");
        var backups = new BackupStore(Store, _temp.Combine("backups"), TimeProvider.System) { IsAutomaticBackupExcluded = _ => true };
        var snapshot = new TextFileReader(Store).Read(path);
        var result = new SafeWriter(Store, backups).Commit(SafeWriter.Prepare(path, snapshot, "after"));
        Assert.True(result.Written); Assert.Null(result.Backup);
        Assert.Empty(backups.List()); Assert.Equal("after", File.ReadAllText(path));
    }

    [Fact]
    public void Prewrite_exclusion_does_not_suppress_change_detection()
    {
        var root = _temp.Combine("project"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "file.md"); File.WriteAllText(path, "before");
        var backups = SessionSnapshots.OpenStore(Store, _temp.Combine("session"), TimeProvider.System, null);
        backups.IsAutomaticBackupExcluded = _ => true;
        SnapshotRoot[] roots = [new(root, ExcludedFolderPolicy.None)]; var limits = new SnapshotLimits();
        var start = SessionSnapshots.Capture(roots, [], Store, backups, limits);
        Assert.False(SessionSnapshots.CapturePreWrite(Store, backups, path, limits));
        File.WriteAllText(path, "modified content");
        Assert.Empty(backups.List());
        Assert.Single(ChangeSummaryBuilder.Build(start, ScopeIndex.Build(roots, Store, limits), backups, Store, limits).Items);
    }

    private sealed class NoRecycle : DotDeck.Core.FileOperations.IRecycleBin
    {
        public bool CanRecycle(string path) => throw new InvalidOperationException("Excluded revert must not reach recycle.");
        public DotDeck.Core.FileOperations.RecycleTicket Recycle(string path) => throw new InvalidOperationException();
        public bool Contains(DotDeck.Core.FileOperations.RecycleTicket ticket) => false;
        public void Restore(DotDeck.Core.FileOperations.RecycleTicket ticket) => throw new InvalidOperationException();
    }
}
