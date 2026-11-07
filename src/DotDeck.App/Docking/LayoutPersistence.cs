using System.IO;
using AvalonDock;
using AvalonDock.Layout;
using AvalonDock.Serializer.Xml;
using DotDeck.App.ViewModels;
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

    private readonly string _layoutDir;
    private readonly IAppLog _log;

    public LayoutPersistence(string layoutDir, IAppLog log)
    {
        _layoutDir = layoutDir;
        _log = log;
    }

    public string LastLayoutPath => Path.Combine(_layoutDir, LastLayoutFileName);

    public LayoutRestoreResult TryRestore(DockingManager manager, ShellViewModel shell)
    {
        var path = LastLayoutPath;
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
            _log.Info("레이아웃을 복원했습니다.");
            return LayoutRestoreResult.Restored;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warning($"레이아웃 복원 실패, 기본 레이아웃을 사용합니다: {path}", ex);
            Reset(manager, shell);
            return LayoutRestoreResult.Failed;
        }
    }

    public void Save(DockingManager manager)
    {
        var path = LastLayoutPath;
        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(_layoutDir);
            new XmlLayoutSerializer(manager).Serialize(temp);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warning($"레이아웃 저장 실패: {path}", ex);
            TryDelete(temp);
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
