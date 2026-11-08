namespace DotDeck.Core.Hooks;

/// <summary>Product-provided event samples and process environment; the runner never guesses either.</summary>
public sealed record HookTestProfile(
    string Product,
    IReadOnlyList<string> Events,
    Func<string, string, string, string> Sample,
    Func<string, IReadOnlyDictionary<string, string>> Environment);
