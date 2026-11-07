namespace DotDeck.Core;

/// <summary>
/// Marker type for locating the DotDeck.Core assembly (tests, reflection).
/// </summary>
public static class CoreAssembly
{
    public static System.Reflection.Assembly Instance => typeof(CoreAssembly).Assembly;
}
