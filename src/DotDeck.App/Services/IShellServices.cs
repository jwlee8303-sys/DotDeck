namespace DotDeck.App.Services;

/// <summary>Files on the clipboard, as Windows Explorer puts them there.</summary>
/// <param name="IsCut">True when they were cut (paste moves them).</param>
public sealed record ClipboardFiles(IReadOnlyList<string> Paths, bool IsCut);

/// <summary>Clipboard access for view models (keeps WPF types out of them).</summary>
public interface IClipboardService
{
    /// <summary>Returns false when the clipboard is busy (another process holds it).</summary>
    bool TrySetText(string text);

    /// <summary>Puts files on the clipboard in the Explorer format (paste works in Windows Explorer too).</summary>
    bool TrySetFiles(IReadOnlyList<string> paths, bool cut);

    /// <summary>Files on the clipboard, or null when there are none (or the clipboard is busy).</summary>
    ClipboardFiles? TryGetFiles();

    /// <summary>Clears the clipboard if it still holds exactly these cut files (after they were pasted).</summary>
    void ClearCutFiles(IReadOnlyList<string> paths);
}

/// <summary>Launches external programs (Windows Explorer, VS Code, terminals — decision Q13).</summary>
public interface IShellLauncher
{
    /// <summary>Opens Explorer at <paramref name="path"/>: a folder is opened, a file is selected in its folder.</summary>
    void RevealInExplorer(string path);

    /// <summary>Opens a file or folder in VS Code. Returns false when VS Code was not found.</summary>
    bool OpenInVsCode(string path);

    /// <summary>Opens Windows Terminal (or PowerShell when it is missing) in <paramref name="directory"/>.</summary>
    void OpenTerminal(string directory);
}
