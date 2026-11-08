namespace DotDeck.Core.Documents;

/// <summary>Host-provided protection for backup bytes. Core has no Windows dependency.</summary>
public interface IBackupProtector
{
    byte[] Protect(byte[] bytes);
    byte[] Unprotect(byte[] bytes);
}
