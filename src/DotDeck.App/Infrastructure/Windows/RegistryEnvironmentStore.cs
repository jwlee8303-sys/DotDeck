using System.IO;
using System.Runtime.InteropServices;
using DotDeck.Core.SystemConfig;
using Microsoft.Win32;

namespace DotDeck.App.Infrastructure.Windows;

/// <summary>Persistent environment variables in the registry (HKCU\Environment, HKLM\...\Session Manager\Environment).</summary>
public sealed partial class RegistryEnvironmentStore(bool isAdministrator) : IEnvironmentStore
{
    private const string MachineKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    public bool CanWrite(EnvironmentScope scope) => scope == EnvironmentScope.User || isAdministrator;

    public IReadOnlyDictionary<string, string> Read(EnvironmentScope scope)
    {
        var result = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var key = Open(scope, writable: false);
        if (key is null)
        {
            return result;
        }

        foreach (var name in key.GetValueNames().Where(n => n.Length > 0))
        {
            // DoNotExpand keeps %VARS% as written (REG_EXPAND_SZ).
            result[name] = key.GetValue(name, string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? string.Empty;
        }

        return result;
    }

    public void Set(EnvironmentScope scope, string name, string? value)
    {
        if (!CanWrite(scope))
        {
            throw new UnauthorizedAccessException("시스템 환경변수는 관리자 권한이 필요합니다.");
        }

        using var key = Open(scope, writable: true) ?? throw new IOException("환경변수 레지스트리 키를 열 수 없습니다.");
        if (value is null)
        {
            key.DeleteValue(name, throwOnMissingValue: false);
            return;
        }

        // Keep REG_EXPAND_SZ for existing expandable values and for values that reference variables.
        var kind = key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase) && key.GetValueKind(name) == RegistryValueKind.ExpandString
            || value.Contains('%', StringComparison.Ordinal)
            ? RegistryValueKind.ExpandString
            : RegistryValueKind.String;
        key.SetValue(name, value, kind);
    }

    public void Broadcast()
    {
        const int HwndBroadcast = 0xffff;
        const int WmSettingChange = 0x001A;
        const int SmtoAbortIfHung = 0x0002;
        _ = SendMessageTimeout(HwndBroadcast, WmSettingChange, 0, "Environment", SmtoAbortIfHung, 2000, out _);
    }

    private static RegistryKey? Open(EnvironmentScope scope, bool writable) => scope == EnvironmentScope.User
        ? Registry.CurrentUser.OpenSubKey("Environment", writable)
        : Registry.LocalMachine.OpenSubKey(MachineKey, writable);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SendMessageTimeout(nint hWnd, int msg, nint wParam, string lParam, int flags, int timeout, out nint result);
}
