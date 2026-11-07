using DotDeck.Core.Paths;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.Paths;

public class AppPathsResolverTests
{
    private readonly FakeProcessEnvironment _env = new();

    [Fact]
    public void Defaults_to_user_profile_and_real_appdata()
    {
        var paths = AppPathsResolver.Resolve([], _env);

        Assert.Equal(@"C:\Users\tester", paths.Home);
        Assert.Equal(HomeSource.UserProfile, paths.HomeSource);
        Assert.False(paths.IsHomeOverridden);
        Assert.Equal(@"C:\Users\tester\AppData\Roaming\DotDeck", paths.RoamingDataDir);
        Assert.Equal(@"C:\Users\tester\AppData\Local\DotDeck", paths.LocalDataDir);
        Assert.Equal(@"C:\Users\tester\.claude", paths.ClaudeHome);
        Assert.Equal(@"C:\Users\tester\.codex", paths.CodexHome);
        Assert.Equal(@"C:\Users\tester\AppData\Roaming\DotDeck\projects.json", paths.ProjectsFile);
        Assert.Equal(@"C:\Users\tester\AppData\Local\DotDeck\backups", paths.BackupsDir);
    }

    [Fact]
    public void Environment_variable_overrides_profile_and_isolates_data()
    {
        _env.Variables[AppPathsResolver.HomeVariable] = @"D:\sandbox";

        var paths = AppPathsResolver.Resolve([], _env);

        Assert.Equal(@"D:\sandbox", paths.Home);
        Assert.Equal(HomeSource.EnvironmentVariable, paths.HomeSource);
        Assert.True(paths.IsHomeOverridden);
        Assert.Equal(@"D:\sandbox\.dotdeck\roaming", paths.RoamingDataDir);
        Assert.Equal(@"D:\sandbox\.dotdeck\local", paths.LocalDataDir);
    }

    [Fact]
    public void Command_line_overrides_environment_variable()
    {
        _env.Variables[AppPathsResolver.HomeVariable] = @"D:\from-env";

        var paths = AppPathsResolver.Resolve(["--home", @"D:\from-arg"], _env);

        Assert.Equal(@"D:\from-arg", paths.Home);
        Assert.Equal(HomeSource.CommandLine, paths.HomeSource);
        Assert.StartsWith(@"D:\from-arg\.dotdeck", paths.RoamingDataDir, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Supports_equals_syntax_and_quotes()
    {
        var paths = AppPathsResolver.Resolve(["--home=\"D:\\with space\\\""], _env);

        Assert.Equal(@"D:\with space", paths.Home);
    }

    [Fact]
    public void Relative_home_is_resolved_against_current_directory()
    {
        _env.CurrentDirectory = @"C:\work";

        var paths = AppPathsResolver.Resolve(["--home", @"sandbox\home"], _env);

        Assert.Equal(@"C:\work\sandbox\home", paths.Home);
    }

    [Fact]
    public void Data_option_overrides_data_location_even_without_home_override()
    {
        var paths = AppPathsResolver.Resolve(["--data", @"E:\dd-data"], _env);

        Assert.Equal(@"C:\Users\tester", paths.Home);
        Assert.Equal(@"E:\dd-data\roaming", paths.RoamingDataDir);
        Assert.Equal(@"E:\dd-data\local", paths.LocalDataDir);
    }

    [Fact]
    public void Blank_environment_variable_is_ignored()
    {
        _env.Variables[AppPathsResolver.HomeVariable] = "   ";

        var paths = AppPathsResolver.Resolve([], _env);

        Assert.Equal(HomeSource.UserProfile, paths.HomeSource);
    }

    [Theory]
    [InlineData("--home")]
    [InlineData("--home", "--data", @"E:\x")]
    [InlineData("--home=")]
    public void Missing_or_empty_option_value_throws(params string[] args)
    {
        Assert.Throws<AppPathsException>(() => AppPathsResolver.Resolve(args, _env));
    }

    [Fact]
    public void Trailing_separator_is_trimmed()
    {
        var paths = AppPathsResolver.Resolve(["--home", @"D:\sandbox\"], _env);

        Assert.Equal(@"D:\sandbox", paths.Home);
    }
}
