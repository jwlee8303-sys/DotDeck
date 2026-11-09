using System.IO;
using DotDeck.App.ViewModels.Dialogs;
using DotDeck.Core.AiCli.Launch;
using DotDeck.Core.AiCli.Sessions;
using DotDeck.Core.Files;
using DotDeck.Core.Paths;
using DotDeck.Core.Projects;

namespace DotDeck.App.Tests;

public sealed class SessionLaunchViewModelTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "DotDeck-launch-test-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly List<AiSessionStartRequest> _previews = [];

    public SessionLaunchViewModelTests()
    {
        _paths = new AppPaths(Path.Combine(_temp, "home"), HomeSource.CommandLine, Path.Combine(_temp, "roaming"), Path.Combine(_temp, "local"));
        Directory.CreateDirectory(_paths.ClaudeHome);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private ProjectDefinition Folder(string id, bool create = true, bool hidden = false)
    {
        var root = Path.Combine(_temp, id);
        if (create)
        {
            Directory.CreateDirectory(root);
        }

        return new ProjectDefinition { Id = id, Kind = ProjectKind.Folder, Name = id.ToUpperInvariant(), Root = root, Hidden = hidden };
    }

    private static ProjectDefinition Builtin(string id, BuiltinProject which) =>
        new() { Id = id, Kind = ProjectKind.Builtin, Builtin = which, Name = id };

    private SessionLaunchViewModel Create(IReadOnlyList<ProjectDefinition> projects, string? primary, IReadOnlyList<string>? remembered = null) =>
        new(projects, _paths, PhysicalFileStore.Instance, primary, _ => remembered ?? [], request =>
        {
            _previews.Add(request);
            return "claude -p " + request.Prompt;
        });

    [Fact]
    public void Lists_only_selectable_primaries_and_preselects_the_given_project()
    {
        ProjectDefinition[] projects =
        [
            Folder("a"), Folder("b"), Folder("missing", create: false), Folder("hidden", hidden: true),
            Builtin("claude", BuiltinProject.Claude), Builtin("system", BuiltinProject.System),
        ];

        var vm = Create(projects, "b");

        // Disabled projects are listed with their reason but are not selectable; system and hidden ones are not listed.
        Assert.Equal(["a", "b", "missing", "claude"], vm.PrimaryChoices.Select(c => c.Id));
        Assert.False(vm.PrimaryChoices.Single(c => c.Id == "missing").IsSelectable);
        Assert.Equal("b", vm.Primary?.Id);
        // Additional rows show disabled projects with the reason, never the primary, hidden or system ones.
        Assert.Equal(["a", "missing", "claude"], vm.AdditionalChoices.Select(c => c.Id));
        Assert.False(vm.AdditionalChoices.Single(c => c.Id == "missing").IsSelectable);
        Assert.Equal("폴더 없음", vm.AdditionalChoices.Single(c => c.Id == "missing").Warning);
    }

    [Fact]
    public void Global_project_is_never_the_default_primary_but_can_be_named_explicitly()
    {
        ProjectDefinition[] projects = [Builtin("claude", BuiltinProject.Claude), Folder("missing", create: false), Folder("a")];

        Assert.Equal("a", Create(projects, null).Primary?.Id);
        Assert.Equal("a", Create(projects, "missing").Primary?.Id);
        Assert.Equal("claude", Create(projects, "claude").Primary?.Id);
        Assert.Null(Create([Builtin("claude", BuiltinProject.Claude)], null).Primary);
    }

    [Theory]
    [InlineData(@"D:\notes.md", "drive")]
    [InlineData(@"C:\work\web\src\app.ts", "web")]
    [InlineData(@"C:\work\website\a.md", "work")]
    [InlineData(@"E:\other\a.md", null)]
    public void Owner_of_a_document_is_the_deepest_project_root(string path, string? expected)
    {
        (string, string?)[] projects = [("drive", @"D:\"), ("work", @"C:\work"), ("web", @"C:\work\web\"), ("none", null)];

        Assert.Equal(expected, SessionLaunchViewModel.OwnerOf(path, projects));
    }

    [Fact]
    public void Read_only_preset_store_disables_saving_presets()
    {
        var vm = new SessionLaunchViewModel([Folder("a")], _paths, PhysicalFileStore.Instance, "a", _ => [], _ => string.Empty, canSavePresets: false)
        {
            Prompt = "x",
            SaveAsPreset = true,
        };

        Assert.False(vm.CanSavePresets);
        Assert.NotNull(vm.TryBuildRequest()); // no preset name needed when presets cannot be saved
    }

    [Fact]
    public void Remembered_additional_projects_are_preselected_but_never_unselectable_ones()
    {
        ProjectDefinition[] projects = [Folder("a"), Folder("b"), Folder("missing", create: false)];

        var vm = Create(projects, "a", remembered: ["b", "missing"]);

        Assert.Equal(["b"], vm.AdditionalChoices.Where(c => c.IsSelected).Select(c => c.Id));
    }

    [Fact]
    public void Global_project_in_the_scope_shows_the_warning()
    {
        ProjectDefinition[] projects = [Folder("a"), Builtin("claude", BuiltinProject.Claude)];
        var vm = Create(projects, "a");
        Assert.False(vm.IncludesGlobalProject);

        vm.AdditionalChoices.Single(c => c.Id == "claude").IsSelected = true;

        Assert.True(vm.IncludesGlobalProject);
        Assert.Contains(_previews, p => p.AdditionalProjectIds.Contains("claude"));
    }

    [Fact]
    public void Request_needs_a_prompt_and_a_preset_name_when_saving()
    {
        var vm = Create([Folder("a"), Folder("b")], "a");
        vm.IsCommandRun = true;
        Assert.Null(vm.TryBuildRequest());
        Assert.NotNull(vm.Error);

        vm.Prompt = "  README 정리  ";
        vm.SaveAsPreset = true;
        Assert.Null(vm.TryBuildRequest());
        Assert.Contains("프리셋", vm.Error, StringComparison.Ordinal);

        vm.PresetName = "문서";
        vm.AdditionalChoices.Single(c => c.Id == "b").IsSelected = true;
        vm.AllowReadOnlyShell = true;
        var request = vm.TryBuildRequest();

        Assert.Null(vm.Error);
        Assert.NotNull(request);
        Assert.Equal("a", request.PrimaryProjectId);
        Assert.Equal(["b"], request.AdditionalProjectIds);
        Assert.Equal("README 정리", request.Prompt);
        Assert.Equal((CliKind.Claude, LaunchMode.NonInteractive), (request.Cli, request.Mode));
        Assert.True(request.AllowReadOnlyShell);
    }

    [Fact]
    public void Preview_follows_the_choices()
    {
        var vm = Create([Folder("a")], "a");
        vm.IsCommandRun = true;
        Assert.Equal("claude -p (프롬프트)", vm.CommandLinePreview);

        vm.Prompt = "hello";

        Assert.Equal("claude -p hello", vm.CommandLinePreview);
    }

    [Fact]
    public void Interactive_is_the_default_and_needs_no_prompt()
    {
        var vm = Create([Folder("a")], "a");
        vm.AllowReadOnlyShell = true;
        vm.Prompt = "무시되는 프롬프트";

        Assert.True(vm.IsInteractive);
        Assert.False(vm.IsCommandRun);
        var request = vm.TryBuildRequest();

        Assert.NotNull(request);
        Assert.Equal(LaunchMode.Interactive, request.Mode);
        Assert.Null(request.Prompt);
        Assert.False(request.AllowReadOnlyShell); // a command-run option only
        Assert.Equal(LaunchMode.Interactive, _previews[^1].Mode);
        Assert.Null(_previews[^1].Prompt);
    }

    [Fact]
    public void Switching_modes_refreshes_the_preview()
    {
        var vm = Create([Folder("a")], "a");
        var before = _previews.Count;

        vm.IsCommandRun = true;
        vm.IsInteractive = true;

        Assert.Equal([LaunchMode.NonInteractive, LaunchMode.Interactive], _previews.Skip(before).Select(p => p.Mode));
    }

    [Fact]
    public void Without_selectable_projects_nothing_can_start()
    {
        var vm = Create([Folder("missing", create: false)], null);

        Assert.False(vm.HasSelectableProject);
        Assert.Null(vm.Primary);
        Assert.Null(vm.TryBuildRequest());
    }
}
