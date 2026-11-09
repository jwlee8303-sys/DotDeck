using DotDeck.Core.AiCli.Snapshots;
using DotDeck.Core.Documents;
using DotDeck.Core.Explorer;
using DotDeck.Core.Files;
using DotDeck.Core.Tests.FileOperations;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.AiCli;

/// <summary>CLI system records in a CLI config folder are neither backed up nor reviewed (DESIGN "CLI 시스템 기록").</summary>
public sealed class SystemRecordTests : IDisposable
{
    private static readonly IFileStore Store = PhysicalFileStore.Instance;

    private readonly TempDirectory _temp = new();
    private readonly string _config;
    private readonly string _session;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero));
    private readonly SnapshotLimits _limits = new();

    public SystemRecordTests()
    {
        _config = _temp.Combine(".claude");
        _session = _temp.Combine("session");
        Directory.CreateDirectory(_config);
        Directory.CreateDirectory(_session);
    }

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData(".claude.json", true)]
    [InlineData(".CLAUDE.JSON", true)]
    [InlineData("history.jsonl", true)]
    [InlineData(@"backups\.claude.json.backup.1", true)]
    [InlineData(@"plugins\synced\x\.marketplaces.json", true)]
    [InlineData(@"plugins\mine\manifest.json", false)]
    [InlineData(@"skills\synced\a\manifest.json", true)]
    [InlineData(@"skills\mine\SKILL.md", false)]
    [InlineData("settings.json", false)]
    [InlineData("test.md", false)]
    [InlineData(@"agents\reviewer.md", false)]
    [InlineData(@"sub\.claude.json", false)] // file entries are rooted at the config folder
    public void Builtin_claude_catalog_separates_cli_records_from_user_files(string relative, bool system)
    {
        Assert.Equal(system, SystemRecordPolicy.Claude.IsSystemFile(relative));
    }

    [Fact]
    public void Builtin_codex_catalog_loads()
    {
        Assert.True(SystemRecordPolicy.Codex.IsSystemFolder("sessions"));
        Assert.False(SystemRecordPolicy.Codex.IsSystemFile("config.toml"));
    }

    [Fact]
    public void Entries_that_could_leave_the_config_folder_are_ignored()
    {
        var policy = new SystemRecordPolicy(["/../outside.txt", "C:/abs/", "/a/./b/", "", "  ", "/ok.json", "folder/"]);

        Assert.False(policy.IsSystemFile(@"..\outside.txt"));
        Assert.False(policy.IsSystemFolder(@"C:\abs"));
        Assert.True(policy.IsSystemFile("ok.json"));
        Assert.True(policy.IsSystemFolder(@"folder\deeper"));
        Assert.False(policy.IsSystemFolder("ok.json"));
    }

    [Theory]
    [InlineData("""{ "paths": [] }""")]
    [InlineData("""{ "schemaVersion": 2, "paths": [] }""")]
    [InlineData("""{ "schemaVersion": 1, "paths": ["/a", 1] }""")]
    [InlineData("""{ "schemaVersion": 1 }""")]
    [InlineData("not json")]
    public void Invalid_catalogs_are_rejected(string json)
    {
        Assert.Throws<InvalidDataException>(() => SystemRecordPolicy.Parse(json));
    }

    [Fact]
    public void Cli_records_are_not_captured_or_summarized_but_user_and_unknown_changes_are()
    {
        var state = Put(".claude.json", "{\"numStartups\":1}");
        var backup = Put(@"backups\.claude.json.backup.1", "{}");
        var userFile = Put("test.md", "원래");
        var unknown = Put("settings.json", "{}");
        var roots = Roots(SystemRecordPolicy.Claude);

        var start = SessionSnapshots.Capture(roots, [state], Store, Backups(), _limits);

        // Neither indexed nor captured, even when open in the editor.
        Assert.DoesNotContain(state, start.Index.Files.Keys);
        Assert.DoesNotContain(backup, start.Index.Files.Keys);
        Assert.Equal(2, start.CapturedFiles); // test.md, settings.json
        Assert.Empty(Backups().ListFor(state));

        // The CLI rewrites its own records; the user's file and an unlisted file change too.
        Change(state, "{\"numStartups\":2}");
        Put(@"sessions\1.key", "k");
        Put(@"plugins\synced\a\.last-complete-round", "1");
        Change(userFile, "AI가 바꿈");
        Change(unknown, "{\"theme\":\"dark\"}");

        var summary = Summary(start, roots);

        Assert.Equal([unknown, userFile], summary.Items.Select(i => i.Path).Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(summary.Items, i => Assert.NotNull(i.PreviousFile));
    }

    [Fact]
    public void A_tool_write_to_a_cli_record_is_still_a_user_change()
    {
        var state = Put(".claude.json", "원래 상태");
        var roots = Roots(SystemRecordPolicy.Claude);
        var start = SessionSnapshots.Capture(roots, [], Store, Backups(), _limits);

        // The guard runs before Claude's Write tool and keeps the original.
        Assert.True(SessionSnapshots.CapturePreWrite(Store, Backups(), state, _limits));
        Change(state, "AI가 고친 상태");

        var item = Assert.Single(Summary(start, roots).Items);
        Assert.Equal(state, item.Path);
        Assert.Equal(ChangeKind.Modified, item.Kind);
        Assert.Null(item.RevertBlockedReason);
    }

    [Fact]
    public void A_root_without_a_policy_keeps_every_file()
    {
        var state = Put(".claude.json", "1");
        var start = SessionSnapshots.Capture(Roots(SystemRecordPolicy.None), [], Store, Backups(), _limits);

        Assert.Contains(state, start.Index.Files.Keys);
    }

    [Fact]
    public void An_open_system_record_is_not_captured_when_only_open_documents_are_backed_up()
    {
        var state = Put(".claude.json", "{}");
        var user = Put("settings.json", "{}");
        var start = SessionSnapshots.Capture(Roots(SystemRecordPolicy.Claude), [state, user], Store, Backups(),
            _limits with { FullCaptureMaxFiles = 0 });

        Assert.False(start.FullCapture);
        Assert.Equal(1, start.CapturedFiles);
        Assert.Empty(Backups().ListFor(state));
        Assert.Single(Backups().ListFor(user));
    }

    [Fact]
    public void Unknown_created_and_deleted_files_remain_user_changes()
    {
        var deleted = Put("unknown.md", "original");
        var roots = Roots(SystemRecordPolicy.Claude);
        var start = SessionSnapshots.Capture(roots, [], Store, Backups(), _limits);
        File.Delete(deleted);
        var created = Put("new-record.json", "{}");

        var items = Summary(start, roots).Items;
        Assert.Equal(ChangeKind.Created, items.Single(i => i.Path == created).Kind);
        Assert.NotNull(items.Single(i => i.Path == deleted).PreviousFile);
    }

    [Fact]
    public void An_unchanged_tool_target_is_not_reported_again_by_the_provenance_record()
    {
        var state = Put(".claude.json", "unchanged");
        var roots = Roots(SystemRecordPolicy.Claude);
        var start = SessionSnapshots.Capture(roots, [], Store, Backups(), _limits);
        Assert.True(SessionSnapshots.CapturePreWrite(Store, Backups(), state, _limits));

        Assert.Empty(Summary(start, roots).Items);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Tool_writes_without_capturable_content_are_still_reviewed(bool existed, bool binary)
    {
        var path = Path.Combine(_config, "cache", "tool.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (existed)
        {
            File.WriteAllBytes(path, binary ? [0, 1, 0] : new byte[32]);
        }

        var roots = Roots(SystemRecordPolicy.Claude);
        var start = SessionSnapshots.Capture(roots, [], Store, Backups(), _limits);
        Assert.False(SessionSnapshots.CapturePreWrite(Store, Backups(), path, _limits with { MaxContentBytes = binary ? 100 : 8 }));
        File.WriteAllText(path, "tool change");

        var item = Assert.Single(Summary(start, roots).Items);
        Assert.Equal(path, item.Path);
        Assert.NotNull(item.RevertBlockedReason); // No original content, so review must not offer an unsafe revert.
    }

    [Fact]
    public void A_record_created_rewritten_and_deleted_in_the_session_is_never_recreated()
    {
        var roots = Roots(SystemRecordPolicy.Claude);
        var start = SessionSnapshots.Capture(roots, [], Store, Backups(), _limits);
        var state = Path.Combine(_config, ".claude.json");

        // Created by a tool (nothing to capture yet), written again (captures the session's own content), then deleted.
        Assert.False(SessionSnapshots.CapturePreWrite(Store, Backups(), state, _limits));
        File.WriteAllText(state, "세션 중 내용");
        Assert.True(SessionSnapshots.CapturePreWrite(Store, Backups(), state, _limits));
        File.Delete(state);

        var item = Assert.Single(Summary(start, roots).Items);
        Assert.Equal(ChangeKind.Deleted, item.Kind);
        Assert.True(item.StartStateUnknown);
        Assert.Contains("확인할 수 없어", item.RevertBlockedReason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_catalog_follows_a_root_above_or_inside_the_config_folder()
    {
        var home = _temp.Path;
        var claude = SystemRecordPolicy.Claude;

        var parent = claude.RelativeTo(_config, home);
        Assert.True(parent.IsSystemFile(@".claude\.claude.json"));
        Assert.True(parent.IsSystemFolder(@".claude\backups"));
        Assert.False(parent.IsSystemFile(".claude.json")); // the home's own file is not the CLI folder's
        Assert.False(parent.IsSystemFile(@".claude\test.md"));

        var skills = claude.RelativeTo(_config + @"\", Path.Combine(_config, "SKILLS"));
        Assert.True(skills.IsSystemFolder("synced"));
        Assert.False(skills.IsSystemFile(@"mine\SKILL.md"));

        var inside = claude.RelativeTo(_config, Path.Combine(_config, "plugins", "cache"));
        Assert.True(inside.IsSystemFile("anything.json"));

        Assert.True(claude.RelativeTo(_config, _temp.Combine("other")).IsEmpty);
        Assert.Same(claude, claude.RelativeTo(_config.ToUpperInvariant(), _config));
    }

    [Fact]
    public void A_home_root_leaves_out_the_records_of_both_clis()
    {
        var codex = _temp.Combine(".codex");
        Put(".claude.json", "{}");
        Put("test.md", "사용자");
        Directory.CreateDirectory(Path.Combine(codex, "sessions"));
        File.WriteAllText(Path.Combine(codex, "sessions", "s.jsonl"), "{}");
        File.WriteAllText(Path.Combine(codex, "config.toml"), "model = \"x\"");
        var policy = SystemRecordPolicy.Combine(
            SystemRecordPolicy.Claude.RelativeTo(_config, _temp.Path),
            SystemRecordPolicy.Codex.RelativeTo(codex, _temp.Path));
        SnapshotRoot[] roots = [new SnapshotRoot(_temp.Path, ExcludedFolderPolicy.None) { SystemRecords = policy }];

        var files = ScopeIndex.Build(roots, Store, _limits).Files.Keys.Select(p => Path.GetRelativePath(_temp.Path, p)).ToList();

        Assert.Contains(@".claude\test.md", files);
        Assert.Contains(@".codex\config.toml", files);
        Assert.DoesNotContain(@".claude\.claude.json", files);
        Assert.DoesNotContain(@".codex\sessions\s.jsonl", files);
    }

    private SnapshotRoot[] Roots(SystemRecordPolicy policy) =>
        [new SnapshotRoot(_config, new ExcludedFolderPolicy(ExcludedFolderPolicy.CommonDefaults)) { SystemRecords = policy }];

    private BackupStore Backups() => SessionSnapshots.OpenStore(Store, _session, _time, protector: null);

    private ChangeSummary Summary(SessionSnapshot start, SnapshotRoot[] roots) =>
        ChangeSummaryBuilder.Build(start, ScopeIndex.Build(roots, Store, _limits), Backups(), Store, _limits);

    private string Put(string relative, string text)
    {
        var path = Path.Combine(_config, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static void Change(string path, string text)
    {
        var before = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, before.AddSeconds(5));
    }
}
