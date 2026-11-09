using System.Text;
using DotDeck.Core.AiCli.Guard;
using DotDeck.Core.AiCli.Snapshots;
using DotDeck.Core.Documents;
using DotDeck.Core.Explorer;
using DotDeck.Core.Files;
using DotDeck.Core.Tests.FileOperations;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.AiCli;

public sealed class SnapshotTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _root;
    private readonly string _session;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero));
    private readonly SnapshotLimits _limits = new();

    public SnapshotTests()
    {
        _root = _temp.Combine("proj");
        _session = _temp.Combine("session");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_session);
    }

    public void Dispose() => _temp.Dispose();

    private static readonly IFileStore Store = PhysicalFileStore.Instance;

    private string Put(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>Writes new content and moves the time forward so size-and-time comparison always sees it.</summary>
    private static void Change(string path, string text)
    {
        var before = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, before.AddSeconds(5));
    }

    private BackupStore SessionBackups() => SessionSnapshots.OpenStore(Store, _session, _time, protector: null);

    private SnapshotRoot[] Roots => [new SnapshotRoot(_root, new ExcludedFolderPolicy(ExcludedFolderPolicy.CommonDefaults))];

    private SessionSnapshot Capture(SnapshotLimits? limits = null, params string[] open) =>
        SessionSnapshots.Capture(Roots, open, Store, SessionBackups(), limits ?? _limits);

    private ChangeSummary Summary(SessionSnapshot start, SnapshotLimits? limits = null) =>
        ChangeSummaryBuilder.Build(start, ScopeIndex.Build(Roots, Store, limits ?? _limits), SessionBackups(), Store, limits ?? _limits);

    [Fact]
    public void Small_scope_is_fully_captured_and_changes_are_summarized()
    {
        var modified = Put("a.txt", "원래 내용");
        var deleted = Put(@"sub\b.md", "# b");
        var touched = Put("same.txt", "그대로");
        var secret = Put(".env.local", "KEY=1");
        Put(@"node_modules\x.js", "excluded");
        File.WriteAllBytes(Path.Combine(_root, "image.bin"), [0, 1, 2, 0, 3]);

        var start = Capture();

        Assert.True(start.FullCapture);
        Assert.Equal(3, start.CapturedFiles); // a, b, same (not the secret, binary or excluded files)
        Assert.DoesNotContain(start.Index.Files.Keys, k => k.Contains("node_modules", StringComparison.Ordinal));

        Change(modified, "AI가 바꾼 내용");
        File.Delete(deleted);
        var created = Put("new.txt", "새 파일");
        File.SetLastWriteTimeUtc(touched, File.GetLastWriteTimeUtc(touched).AddSeconds(5));
        Change(secret, "KEY=2");
        Change(Path.Combine(_root, "node_modules", "x.js"), "changed but excluded");

        var summary = Summary(start);

        Assert.False(summary.IndexTruncated);
        Assert.Equal(
            [(".env.local", ChangeKind.Modified), ("a.txt", ChangeKind.Modified), ("new.txt", ChangeKind.Created), ("b.md", ChangeKind.Deleted)],
            summary.Items.Select(i => (Path.GetFileName(i.Path), i.Kind)).OrderBy(x => x.Item1 == "b.md").ThenBy(x => x.Item1, StringComparer.Ordinal));
        Assert.NotNull(summary.Items.Single(i => i.Path == modified).PreviousFile);
        Assert.NotNull(summary.Items.Single(i => i.Path == deleted).PreviousFile);
        Assert.Contains("비밀 파일", summary.Items.Single(i => i.Path == secret).RevertBlockedReason, StringComparison.Ordinal);
        Assert.Null(summary.Items.Single(i => i.Path == created).RevertBlockedReason);
    }

    [Fact]
    public void Large_scope_captures_open_documents_and_pre_write_fills_the_rest()
    {
        var open = Put("open.md", "열린 문서");
        var other = Put("other.md", "다른 파일");
        var limits = _limits with { FullCaptureMaxFiles = 1 };

        var start = Capture(limits, open);
        Assert.False(start.FullCapture);
        Assert.Equal(1, start.CapturedFiles);

        Change(other, "바뀜");
        Assert.Contains("기록되지 않았습니다", Summary(start, limits).Items.Single(i => i.Path == other).RevertBlockedReason, StringComparison.Ordinal);

        // The guard captures before a write; a second capture of the same path keeps the first (true) original.
        var third = Put("third.md", "원본");
        Assert.True(SessionSnapshots.CapturePreWrite(Store, SessionBackups(), third, limits));
        Change(third, "첫 수정");
        Assert.False(SessionSnapshots.CapturePreWrite(Store, SessionBackups(), third, limits));
        Change(third, "두 번째 수정");

        var item = Summary(start, limits).Items.Single(i => i.Path == third);
        // The complete start index shows third.md did not exist then: a later capture does not make it pre-existing.
        Assert.Equal(ChangeKind.Created, item.Kind);
        var kept = SessionBackups().ListFor(third).Single();
        Assert.Equal("원본", Encoding.UTF8.GetString(SessionBackups().ReadContent(kept.Set, kept.File)));
    }

    [Fact]
    public void Pre_write_never_captures_secret_binary_or_large_files()
    {
        var secret = Put("id_rsa", "key");
        var large = Put("big.txt", new string('x', 100));
        var binary = Path.Combine(_root, "b.dat");
        File.WriteAllBytes(binary, [1, 0, 2]);
        var limits = _limits with { MaxContentBytes = 50 };

        Assert.False(SessionSnapshots.CapturePreWrite(Store, SessionBackups(), secret, limits));
        Assert.False(SessionSnapshots.CapturePreWrite(Store, SessionBackups(), large, limits));
        Assert.False(SessionSnapshots.CapturePreWrite(Store, SessionBackups(), binary, limits));
        Assert.False(SessionSnapshots.CapturePreWrite(Store, SessionBackups(), Path.Combine(_root, "missing.txt"), limits));
        Assert.Empty(SessionBackups().List());
    }

    [Fact]
    public void Truncated_index_is_reported_and_does_not_invent_deletions()
    {
        for (var i = 0; i < 5; i++)
        {
            Put($"f{i}.txt", "x");
        }

        var limits = _limits with { MaxIndexedFiles = 3 };
        var start = Capture(limits);

        Assert.True(start.Index.Truncated);
        var summary = Summary(start, limits);
        Assert.True(summary.IndexTruncated);
        Assert.DoesNotContain(summary.Items, i => i.Kind == ChangeKind.Deleted);
    }

    [Fact]
    public void Revert_restores_recycles_and_refuses_files_changed_again()
    {
        var modified = Put("a.txt", "원래");
        var deleted = Put("b.txt", "지워질 파일");
        var start = Capture();
        Change(modified, "AI 수정");
        File.Delete(deleted);
        var created = Put("c.txt", "AI가 만든 파일");
        var summary = Summary(start);

        var userBackups = new BackupStore(Store, _temp.Combine("user-backups"), _time);
        var bin = new FakeRecycleBin(_temp.Combine("bin"));
        var planner = new RevertPlanner(Store, new SafeWriter(Store, userBackups), bin, _limits, Roots);

        var restored = planner.Revert(summary.Items.Single(i => i.Path == modified));
        Assert.Equal(RevertStatus.Reverted, restored.Status);
        Assert.Equal("원래", File.ReadAllText(modified));
        Assert.Equal("AI 수정", Encoding.UTF8.GetString(userBackups.ReadContent(restored.BeforeRestore!, restored.BeforeRestore!.Files[0])));

        Assert.Equal(RevertStatus.Reverted, planner.Revert(summary.Items.Single(i => i.Path == deleted)).Status);
        Assert.Equal("지워질 파일", File.ReadAllText(deleted));

        var createdItem = summary.Items.Single(i => i.Path == created);
        Change(created, "요약 뒤 사용자가 고침");
        Assert.Equal(RevertStatus.ChangedSinceSummary, planner.Revert(createdItem).Status);
        Assert.True(File.Exists(created));
        Assert.Equal(RevertStatus.Reverted, planner.Revert(createdItem, force: true).Status);
        Assert.False(File.Exists(created));
        Assert.Equal(1, bin.RecycleCount);
    }

    [Fact]
    public void Created_file_without_recycle_bin_is_not_deleted_permanently()
    {
        var start = Capture();
        var created = Put("c.txt", "x");
        var item = Summary(start).Items.Single();
        var bin = new FakeRecycleBin(_temp.Combine("bin"));
        bin.NoRecycleBinFolders.Add(_root);
        var planner = Planner(bin);

        Assert.Equal(RevertStatus.NotRevertible, planner.Revert(item).Status);
        Assert.True(File.Exists(created));
    }

    private RevertPlanner Planner(FakeRecycleBin? bin = null) =>
        new(Store, new SafeWriter(Store, new BackupStore(Store, _temp.Combine("ub"), _time)), bin ?? new FakeRecycleBin(_temp.Combine("bin")), _limits, Roots);

    [Fact]
    public void Created_file_seen_after_a_truncated_start_index_is_never_recycled()
    {
        for (var i = 0; i < 5; i++)
        {
            Put($"f{i}.txt", "x");
        }

        var limits = _limits with { MaxIndexedFiles = 3 };
        var start = Capture(limits);
        var bin = new FakeRecycleBin(_temp.Combine("bin"));

        // Indexing order is not guaranteed: whichever existing file the start index missed now looks "created".
        var created = Summary(start, limits with { MaxIndexedFiles = 50 }).Items.Where(i => i.Kind == ChangeKind.Created).ToList();
        Assert.NotEmpty(created);
        foreach (var item in created)
        {
            Assert.True(item.PossiblyPreexisting);
            Assert.Equal(RevertStatus.NotRevertible, Planner(bin).Revert(item).Status);
            Assert.True(File.Exists(item.Path));
        }

        Assert.Equal(0, bin.RecycleCount);
    }

    [Fact]
    public void File_created_and_written_again_in_the_session_is_still_created_and_reverts_to_recycling()
    {
        var start = Capture();
        var path = Path.Combine(_root, "new.txt");

        // First write: nothing to capture. Second write: the guard captures the AI's own intermediate content.
        Assert.False(SessionSnapshots.CapturePreWrite(Store, SessionBackups(), path, _limits));
        File.WriteAllText(path, "중간");
        Assert.True(SessionSnapshots.CapturePreWrite(Store, SessionBackups(), path, _limits));
        Change(path, "최종");

        var item = Assert.Single(Summary(start).Items);
        Assert.Equal(ChangeKind.Created, item.Kind);
        Assert.False(item.PossiblyPreexisting);

        var bin = new FakeRecycleBin(_temp.Combine("bin"));
        Assert.Equal(RevertStatus.Reverted, Planner(bin).Revert(item).Status);
        Assert.False(File.Exists(path));
        Assert.Equal(1, bin.RecycleCount);
    }

    [Fact]
    public void File_created_rewritten_and_deleted_in_the_session_is_not_reported_as_deleted()
    {
        var start = Capture();
        var path = Path.Combine(_root, "tmp.txt");
        File.WriteAllText(path, "중간");
        Assert.True(SessionSnapshots.CapturePreWrite(Store, SessionBackups(), path, _limits));
        File.Delete(path);

        Assert.Empty(Summary(start).Items);
    }

    [Fact]
    public void Captured_file_changed_with_the_same_size_and_time_is_still_reported()
    {
        var path = Put("a.txt", "abc");
        var start = Capture();
        var time = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "xyz");
        File.SetLastWriteTimeUtc(path, time);

        var item = Assert.Single(Summary(start).Items);
        Assert.Equal(ChangeKind.Modified, item.Kind);
        Assert.Equal(RevertStatus.Reverted, Planner().Revert(item).Status);
        Assert.Equal("abc", File.ReadAllText(path));
    }

    [Fact]
    public void Pre_write_capture_in_an_excluded_folder_is_reported()
    {
        var path = Put(@"node_modules\x.js", "원본");
        var start = Capture();
        Assert.True(SessionSnapshots.CapturePreWrite(Store, SessionBackups(), path, _limits));
        Change(path, "AI 수정");

        var item = Assert.Single(Summary(start).Items);
        Assert.Equal((path, ChangeKind.Modified), (item.Path, item.Kind));
        Assert.NotNull(item.PreviousFile);
    }

    [Fact]
    public void Open_document_beyond_a_truncated_index_is_still_captured()
    {
        for (var i = 0; i < 5; i++)
        {
            Put($"f{i}.txt", "x");
        }

        var open = Put(@"zz\open.md", "열린 문서");
        var excludedOpen = Put(@"node_modules\open.js", "제외 폴더");
        var outside = _temp.Combine("outside.txt");
        File.WriteAllText(outside, "범위 밖");
        var limits = _limits with { MaxIndexedFiles = 1 };

        var start = Capture(limits, open, excludedOpen, outside);

        Assert.True(start.Index.Truncated);
        Assert.Equal(1, start.CapturedFiles);
        Assert.Single(SessionBackups().ListFor(open));
    }

    [Fact]
    public void Pre_write_capture_gives_up_quickly_while_another_capture_holds_the_lock()
    {
        var path = Put("a.txt", "x");
        var backups = SessionBackups();
        using (Store.AcquireLock(Path.Combine(backups.BackupsDirectory, "capture.lock"), TimeSpan.FromSeconds(1)))
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Assert.False(SessionSnapshots.CapturePreWrite(Store, backups, path, _limits with { PreWriteLockTimeout = TimeSpan.FromMilliseconds(100) }));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        }

        Assert.True(SessionSnapshots.CapturePreWrite(Store, backups, path, _limits));
    }

    [Fact]
    public void Revert_refuses_a_target_whose_folder_became_a_junction()
    {
        var path = Put(@"sub\a.txt", "원래");
        var start = Capture();
        Change(path, "AI 수정");
        var item = Assert.Single(Summary(start).Items);

        var outside = _temp.Combine("outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "a.txt"), "범위 밖 파일");
        Directory.Delete(Path.Combine(_root, "sub"), recursive: true);
        if (!TryCreateJunction(Path.Combine(_root, "sub"), outside))
        {
            Assert.Skip("junction creation unavailable");
        }

        var result = Planner().Revert(item, force: true);

        Assert.Equal(RevertStatus.NotRevertible, result.Status);
        Assert.Equal("범위 밖 파일", File.ReadAllText(Path.Combine(outside, "a.txt")));
    }

    [Fact]
    public void Revert_of_a_path_outside_the_roots_is_refused()
    {
        var item = new ChangeItem { Path = _temp.Combine("elsewhere.txt"), Kind = ChangeKind.Created };
        File.WriteAllText(item.Path, "x");

        Assert.Equal(RevertStatus.NotRevertible, Planner().Revert(item, force: true).Status);
        Assert.True(File.Exists(item.Path));
    }

    [Fact]
    public void Conditional_restore_refuses_a_file_that_is_not_in_the_expected_state()
    {
        var path = Put("a.txt", "원래");
        var userBackups = new BackupStore(Store, _temp.Combine("ub"), _time);
        var set = userBackups.Create([new BackupSource(path, File.ReadAllBytes(path))], "test");
        var writer = new SafeWriter(Store, userBackups);
        File.WriteAllText(path, "지금");

        var mismatch = new RestoreExpectation(TextFileReader.ComputeHash(Encoding.UTF8.GetBytes("다른 내용")));
        Assert.Throws<ExternalChangeException>(() => writer.Restore(set, set.Files[0], mismatch));
        Assert.Throws<ExternalChangeException>(() => writer.Restore(set, set.Files[0], new RestoreExpectation(null)));
        Assert.Equal("지금", File.ReadAllText(path));

        writer.Restore(set, set.Files[0], new RestoreExpectation(TextFileReader.ComputeHash(File.ReadAllBytes(path))));
        Assert.Equal("원래", File.ReadAllText(path));

        File.Delete(path);
        Assert.Throws<ExternalChangeException>(() => writer.Restore(set, set.Files[0], mismatch));
        Assert.False(File.Exists(path));
    }

    private static bool TryCreateJunction(string link, string target)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/J", link, target },
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        process!.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
    }

    private string HookInput(string tool, string path) => new System.Text.Json.Nodes.JsonObject
    {
        ["tool_name"] = tool,
        ["cwd"] = _root,
        ["tool_input"] = new System.Text.Json.Nodes.JsonObject { ["file_path"] = path },
    }.ToJsonString();

    [Fact]
    public void Guard_reports_write_targets_for_pre_write_capture()
    {
        var scope = Path.Combine(_session, "scope.json");
        DotDeck.Core.AiCli.Scope.ScopeFile.Write(scope, new DotDeck.Core.AiCli.Scope.ScopeDocument
        {
            SessionId = "s",
            Roots = [new DotDeck.Core.AiCli.Scope.ScopeFileRoot { Path = _root }],
        });
        var calls = new List<(string Session, string Target)>();

        int Run(string tool, string path)
        {
            var json = HookInput(tool, path);
            using var stdin = new MemoryStream(Encoding.UTF8.GetBytes(json));
            return GuardHost.RunGuard([GuardHost.GuardSwitch, scope], stdin, new MemoryStream(), new MemoryStream(),
                new DotDeck.Core.AiCli.Scope.LexicalPathResolver(), (s, t) => calls.Add((s, t)));
        }

        Assert.Equal(0, Run("Edit", @"sub\..\a.txt"));
        Assert.Equal(0, Run("Read", "a.txt"));
        Assert.Equal(0, Run("Write", @"C:\Windows\x.txt"));

        var call = Assert.Single(calls);
        Assert.Equal(_session, call.Session);
        Assert.Equal(Path.Combine(_root, "a.txt"), call.Target);
    }

    [Fact]
    public void Capture_failure_never_changes_the_guard_decision()
    {
        var scope = Path.Combine(_session, "scope.json");
        DotDeck.Core.AiCli.Scope.ScopeFile.Write(scope, new DotDeck.Core.AiCli.Scope.ScopeDocument
        {
            SessionId = "s",
            Roots = [new DotDeck.Core.AiCli.Scope.ScopeFileRoot { Path = _root }],
        });
        var json = HookInput("Write", "a.txt");
        using var stdin = new MemoryStream(Encoding.UTF8.GetBytes(json));
        using var stdout = new MemoryStream();

        var exit = GuardHost.RunGuard([GuardHost.GuardSwitch, scope], stdin, stdout, new MemoryStream(),
            new DotDeck.Core.AiCli.Scope.LexicalPathResolver(), (_, _) => throw new IOException("disk full"));

        Assert.Equal(0, exit);
        Assert.Equal(0, stdout.Length);
    }
}
