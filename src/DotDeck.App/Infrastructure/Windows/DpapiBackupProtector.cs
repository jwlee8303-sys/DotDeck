using System.Security.Cryptography;
using System.IO;
using DotDeck.Core.Documents;

namespace DotDeck.App.Infrastructure.Windows;

public sealed class DpapiBackupProtector : IBackupProtector
{
    public byte[] Protect(byte[] bytes) => ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] bytes)
    {
        try { return ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser); }
        catch (CryptographicException)
        {
            throw new IOException("복원 불가: 백업을 만든 Windows 계정으로 실행해야 합니다. 백업이 손상된 경우에도 복호화할 수 없습니다.");
        }
    }
}
