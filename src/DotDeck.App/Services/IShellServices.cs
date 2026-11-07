namespace DotDeck.App.Services;

/// <summary>Clipboard access for view models (keeps WPF types out of them).</summary>
public interface IClipboardService
{
    /// <summary>Returns false when the clipboard is busy (another process holds it).</summary>
    bool TrySetText(string text);
}

/// <summary>Launches external programs (Windows Explorer; later VS Code and terminals, decision Q13).</summary>
public interface IShellLauncher
{
    /// <summary>Opens Explorer at <paramref name="path"/>: a folder is opened, a file is selected in its folder.</summary>
    void RevealInExplorer(string path);
}
