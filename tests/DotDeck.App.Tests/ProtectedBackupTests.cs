using System.Text;
using System.IO;
using DotDeck.App.Infrastructure.Windows;
using DotDeck.Core.Documents;
using DotDeck.Core.Files;
using DotDeck.Core.Logging;
using DotDeck.Core.Security;

namespace DotDeck.App.Tests;

public sealed class ProtectedBackupTests
{
    [Fact]
    public void Auth_can_be_read_edited_saved_and_backup_decrypts_to_identical_bytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotDeck-crypto-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string secret = "fake-api-credential-984317";
            var file = Path.Combine(root, "auth.json");
            var original = Encoding.UTF8.GetBytes("{\"API_KEY\":\"" + secret + "\"}\r\n");
            File.WriteAllBytes(file, original);
            var store = new BackupStore(PhysicalFileStore.Instance, Path.Combine(root, "backups"), TimeProvider.System, protector: new DpapiBackupProtector());
            var snapshot = new TextFileReader(PhysicalFileStore.Instance).Read(file);
            Assert.DoesNotContain(secret, SecretPresentation.MaskDocument(file, snapshot.Text), StringComparison.Ordinal);
            Assert.True(snapshot.IsSecretFile);

            // Opening a document collects its secrets for the log filter (as TextDocumentViewModel does).
            SecretPresentation.CollectSecrets(file, snapshot.Text, snapshot.IsSecretFile);
            var result = new SafeWriter(PhysicalFileStore.Instance, store).Commit(SafeWriter.Prepare(file, snapshot, "{\"API_KEY\":\"updated-fake\"}\r\n"));
            Assert.True(result.Written);
            var set = Assert.Single(store.List());
            var item = Assert.Single(set.Files);
            Assert.True(item.Protected);
            var ciphertext = File.ReadAllBytes(Path.Combine(set.Directory, item.RelativePath));
            Assert.NotEqual(original, ciphertext);
            Assert.DoesNotContain(secret, Encoding.UTF8.GetString(ciphertext), StringComparison.Ordinal);
            Assert.Equal(original, store.ReadContent(set, item));
            new SafeWriter(PhysicalFileStore.Instance, store).Restore(set, item);
            Assert.Equal(original, File.ReadAllBytes(file));
            var manifest = File.ReadAllText(Path.Combine(set.Directory, BackupStore.ManifestFileName));
            Assert.DoesNotContain(secret, manifest, StringComparison.Ordinal);
            var logDir = Path.Combine(root, "logs");
            var log = new FileAppLog(logDir, TimeProvider.System);
            string? output = null;
            log.EntryWritten += (_, entry) => output = entry.Message;
            log.Write(AppLogLevel.Info, "server output: " + secret);
            Assert.DoesNotContain(secret, output!, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, File.ReadAllText(Directory.GetFiles(logDir).Single()), StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Unreadable_dpapi_payload_reports_restore_unavailable()
    {
        var error = Assert.Throws<IOException>(() => new DpapiBackupProtector().Unprotect([1, 2, 3]));
        Assert.Contains("복원 불가", error.Message, StringComparison.Ordinal);
        Assert.Contains("Windows 계정", error.Message, StringComparison.Ordinal);
    }
}
