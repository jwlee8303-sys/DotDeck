namespace DotDeck.Core.Platform;

/// <summary>
/// Read-only view of the current process environment (variables, special folders, working directory).
/// Abstracted so path resolution can be tested without touching the real environment.
/// </summary>
public interface IProcessEnvironment
{
    string? GetVariable(string name);

    string GetFolderPath(System.Environment.SpecialFolder folder);

    string CurrentDirectory { get; }
}

public sealed class SystemProcessEnvironment : IProcessEnvironment
{
    public static SystemProcessEnvironment Instance { get; } = new();

    private SystemProcessEnvironment()
    {
    }

    public string? GetVariable(string name) => System.Environment.GetEnvironmentVariable(name);

    public string GetFolderPath(System.Environment.SpecialFolder folder) =>
        System.Environment.GetFolderPath(folder, System.Environment.SpecialFolderOption.DoNotVerify);

    public string CurrentDirectory => System.Environment.CurrentDirectory;
}
