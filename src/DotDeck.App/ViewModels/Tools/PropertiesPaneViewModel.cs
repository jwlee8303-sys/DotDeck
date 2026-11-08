using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DotDeck.App.ViewModels.Tools;

/// <summary>One access rule of a file or folder ACL (read-only view, SPEC §8).</summary>
public sealed record AclRow(string Identity, string Access, string Rights, string Inherited);

/// <summary>What a file is to Claude Code (SPEC 7.0), shown above the file details.</summary>
public sealed record RoleDescription(string Title, IReadOnlyList<string> Lines);

/// <summary>Selected item information and its Windows ACL. Viewing only: DotDeck never edits ACLs.</summary>
public sealed partial class PropertiesPaneViewModel() : ToolPaneViewModel("tool.properties", "속성", ToolLocation.Right)
{
    private int _version;
    private int _roleVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItem))]
    private string? _path;

    [ObservableProperty]
    private string _details = string.Empty;

    [ObservableProperty]
    private string _owner = string.Empty;

    [ObservableProperty]
    private string? _aclError;

    [ObservableProperty]
    private IReadOnlyList<AclRow> _rules = [];

    public bool HasItem => Path is not null;

    /// <summary>Describes the role of a path (set by the shell); null for unknown files.</summary>
    public Func<string, RoleDescription?>? DescribeRole { get; init; }

    [ObservableProperty]
    private RoleDescription? _role;

    /// <summary>Re-reads the role of the shown item (after "다음 형식으로 열기").</summary>
    public async void Refresh()
    {
        if (Path is not { } path)
        {
            return;
        }

        var roleVersion = ++_roleVersion;
        var role = await Task.Run(() => Describe(path));
        if (roleVersion == _roleVersion && string.Equals(path, Path, StringComparison.OrdinalIgnoreCase))
        {
            Role = role;
        }
    }

    private RoleDescription? Describe(string path)
    {
        try
        {
            return DescribeRole?.Invoke(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Shows <paramref name="path"/> (null clears). ACL reading runs off the UI thread.</summary>
    public async void Show(string? path)
    {
        if (string.Equals(path, Path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var version = ++_version;
        Path = path;
        Details = string.Empty;
        Owner = string.Empty;
        AclError = null;
        Rules = [];
        Role = null;
        if (path is null)
        {
            return;
        }

        // Role detection checks folders (project roots) and ACL reading may be slow: both run off the UI thread.
        var roleVersion = ++_roleVersion;
        var (result, role) = await Task.Run(() => (Read(path), Describe(path)));
        if (version != _version)
        {
            return;
        }

        (Details, Owner, Rules, AclError) = result;
        if (roleVersion == _roleVersion)
        {
            Role = role;
        }
    }

    private static (string Details, string Owner, IReadOnlyList<AclRow> Rules, string? Error) Read(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (!info.Exists)
            {
                return ("없는 항목입니다.", string.Empty, [], null);
            }

            var details = info is FileInfo file
                ? $"파일 · {file.Length:N0} 바이트 · 수정 {file.LastWriteTime:yyyy-MM-dd HH:mm}{(file.IsReadOnly ? " · 읽기 전용" : string.Empty)}"
                : $"폴더 · 수정 {info.LastWriteTime:yyyy-MM-dd HH:mm}";
            FileSystemSecurity security = info is FileInfo f ? f.GetAccessControl() : ((DirectoryInfo)info).GetAccessControl();
            var owner = Name(security.GetOwner(typeof(SecurityIdentifier)));
            var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()
                .Select(r => new AclRow(
                    Name(r.IdentityReference),
                    r.AccessControlType == AccessControlType.Allow ? "허용" : "거부",
                    r.FileSystemRights.ToString(),
                    r.IsInherited ? "상속" : "직접"))
                .ToList();
            return (details, owner, rules, null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or PrivilegeNotHeldException)
        {
            return (string.Empty, string.Empty, [], $"권한 정보를 읽을 수 없습니다: {ex.Message}");
        }
    }

    private static string Name(IdentityReference? identity)
    {
        if (identity is null)
        {
            return "(없음)";
        }

        try
        {
            return identity.Translate(typeof(NTAccount)).Value;
        }
        catch (IdentityNotMappedException)
        {
            return identity.Value;
        }
    }
}
