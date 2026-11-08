using System.IO;
using AvalonDock;
using AvalonDock.Layout;
using AvalonDock.Serializer.Xml;
using DotDeck.App.ViewModels;
using DotDeck.Core.Layout;
using DotDeck.Core.Logging;

namespace DotDeck.App.Docking;

public enum LayoutRestoreResult
{
    NoSavedLayout,
    Restored,
    Failed,
}

/// <summary>
/// Saves and restores the docking layout (<c>layout\last.xml</c>). Restore failures fall back to the
/// default layout; the app must always start with a usable window.
/// </summary>
public sealed class LayoutPersistence
{
    public const string LastLayoutFileName = "last.xml";

    public const string NamedFolderName = "named";

    private readonly string _layoutDir;
    private readonly IAppLog _log;

    public LayoutPersistence(string layoutDir, IAppLog log)
    {
        _layoutDir = layoutDir;
        _log = log;
    }

    public string LastLayoutPath => Path.Combine(_layoutDir, LastLayoutFileName);

    public string NamedLayoutDir => Path.Combine(_layoutDir, NamedFolderName);

    public LayoutRestoreResult TryRestore(DockingManager manager, ShellViewModel shell) => Load(manager, shell, LastLayoutPath);

    /// <summary>Loads a named layout. Tabs it does not know stay open (AvalonDock re-inserts bound documents).</summary>
    public LayoutRestoreResult TryLoadNamed(DockingManager manager, ShellViewModel shell, string name) =>
        LayoutNames.Validate(name) is null
            ? Load(manager, shell, Path.Combine(NamedLayoutDir, LayoutNames.FileName(name)))
            : LayoutRestoreResult.NoSavedLayout;

    public IReadOnlyList<string> ListNamed()
    {
        try
        {
            return Directory.Exists(NamedLayoutDir)
                ? LayoutNames.FromFileNames(Directory.EnumerateFiles(NamedLayoutDir).Select(f => Path.GetFileName(f)))
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning($"레이아웃 목록을 읽지 못했습니다: {NamedLayoutDir}", ex);
            return [];
        }
    }

    public bool SaveNamed(DockingManager manager, string name) =>
        LayoutNames.Validate(name) is null && SaveTo(manager, NamedLayoutDir, Path.Combine(NamedLayoutDir, LayoutNames.FileName(name)));

    public bool DeleteNamed(string name)
    {
        if (LayoutNames.Validate(name) is not null)
        {
            return false;
        }

        try
        {
            File.Delete(Path.Combine(NamedLayoutDir, LayoutNames.FileName(name)));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning($"레이아웃을 삭제하지 못했습니다: {name}", ex);
            return false;
        }
    }

    private LayoutRestoreResult Load(DockingManager manager, ShellViewModel shell, string path)
    {
        if (!File.Exists(path))
        {
            return LayoutRestoreResult.NoSavedLayout;
        }

        try
        {
            var serializer = new XmlLayoutSerializer(manager);
            serializer.LayoutSerializationCallback += (_, e) =>
            {
                var content = shell.ResolveContent(e.Model.ContentId);
                if (content is null)
                {
                    e.Cancel = true;
                    return;
                }

                e.Content = content;
            };

            serializer.Deserialize(path);
            SyncToolVisibility(manager.Layout, shell);
            _log.Info($"레이아웃을 복원했습니다: {Path.GetFileName(path)}");
            return LayoutRestoreResult.Restored;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warning($"레이아웃 복원 실패, 기본 레이아웃을 사용합니다: {path}", ex);
            Reset(manager, shell);
            return LayoutRestoreResult.Failed;
        }
    }

    public void Save(DockingManager manager) => SaveTo(manager, _layoutDir, LastLayoutPath);

    private bool SaveTo(DockingManager manager, string directory, string path)
    {
        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            new XmlLayoutSerializer(manager).Serialize(temp);
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warning($"레이아웃 저장 실패: {path}", ex);
            TryDelete(temp);
            return false;
        }
    }

    /// <summary>Replaces the layout with the default one. Bound sources are re-inserted by AvalonDock.</summary>
    public static void Reset(DockingManager manager, ShellViewModel shell)
    {
        foreach (var tool in shell.Tools)
        {
            tool.IsVisible = true;
        }

        manager.Layout = DefaultLayout.Create();
    }

    /// <summary>Hidden anchorables in a restored layout must be reflected in the view models.</summary>
    private static void SyncToolVisibility(LayoutRoot layout, ShellViewModel shell)
    {
        var hidden = layout.Hidden.Select(a => a.Content).OfType<ToolPaneViewModel>().ToHashSet();
        foreach (var tool in shell.Tools)
        {
            tool.IsVisible = !hidden.Contains(tool);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
