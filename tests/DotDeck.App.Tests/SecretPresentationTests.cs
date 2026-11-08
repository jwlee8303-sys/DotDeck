using DotDeck.App.ViewModels.Dialogs;
using DotDeck.App.ViewModels.Documents;
using DotDeck.App.ViewModels.Editor;
using DotDeck.Core.Documents;
using DotDeck.Core.Editors;
using DotDeck.Core.Security;

namespace DotDeck.App.Tests;

/// <summary>Tests that switch the process-wide screen share mode must not run in parallel with other tests.</summary>
[CollectionDefinition(nameof(ScreenShareCollection), DisableParallelization = true)]
public sealed class ScreenShareCollection;

[Collection(nameof(ScreenShareCollection))]
public sealed class SecretPresentationTests : IDisposable
{
    private const string Secret = "fake-private-credential-9374";

    public SecretPresentationTests() => SecretPresentation.ScreenShareMode = false;

    public void Dispose() => SecretPresentation.ScreenShareMode = false;

    [Fact]
    public void Screen_share_mode_masks_by_path_without_changing_original()
    {
        SecretPresentation.ScreenShareMode = true;
        var text = "{\"access_token\":\"" + Secret + "\"}";
        Assert.DoesNotContain(Secret, SecretPresentation.MaskDocument("auth.json", text), StringComparison.Ordinal);
        Assert.Contains(Secret, text, StringComparison.Ordinal);
        var toml = "[mcp_servers.test.env]\nSESSION = '" + Secret + "'\n";
        Assert.DoesNotContain(Secret, SecretPresentation.MaskDocument("config.toml", toml), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("config.toml", "[mcp_servers.test.env]\nSESSION = '" + Secret + "'\nbroken = \n")]
    [InlineData("settings.json", "{\"env\": {\"SESSION\": \"" + Secret + "\"},")]
    public void Unparsable_structured_document_is_masked_completely(string path, string text)
    {
        Assert.Equal(SecretMasker.Mask, SecretPresentation.MaskDocument(path, text));
    }

    [Fact]
    public void Link_target_secret_flag_masks_a_harmless_name_completely()
    {
        Assert.Equal(SecretMasker.Mask, SecretPresentation.MaskDocument("notes.txt", "-----BEGIN KEY-----\n" + Secret, isSecretFile: true));
        Assert.Equal("plain text", SecretPresentation.MaskDocument("notes.txt", "plain text"));
    }

    [Fact]
    public void Collected_secrets_are_filtered_from_logs_even_when_screen_share_mode_is_off()
    {
        const string value = "fake-session-value-5521";
        SecretPresentation.CollectSecrets("config.toml", "[mcp_servers.x.env]\nSESSION = '" + value + "'\n");
        Assert.DoesNotContain(value, SecretMasker.MaskText("output: started with " + value), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".env", "# comment\nexport FAKE_ONE=fake-env-value-1111\nOTHER=\"fake-env-value-2222\"\n", "fake-env-value-2222", false)]
    [InlineData("notes.txt", "FAKE_ONE=fake-alias-env-3333\n", "fake-alias-env-3333", true)]
    [InlineData("notes.txt", "{\"refresh\": \"fake-alias-json-4444\"}", "fake-alias-json-4444", true)]
    [InlineData("notes.txt", "-----BEGIN PRIVATE KEY-----\nZmFrZS1wZW0tYm9keS1saW5lLTU1NTU1NTU1\n-----END PRIVATE KEY-----\n", "ZmFrZS1wZW0tYm9keS1saW5lLTU1NTU1NTU1", true)]
    public void Unstructured_or_aliased_secret_files_register_values_for_the_log_filter(string name, string text, string value, bool isSecretFile)
    {
        SecretPresentation.CollectSecrets(name, text, isSecretFile);
        Assert.DoesNotContain(value, SecretMasker.MaskText("output: " + value), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("export SESSION=\"fake-review-token-9034\" # local", "fake-review-token-9034")]
    [InlineData("SESSION='fake-review-token-9035' # local", "fake-review-token-9035")]
    [InlineData("SESSION=fake-review-token-9036 # local", "fake-review-token-9036")]
    [InlineData("SESSION=\"fake-#hash-token-9037\"", "fake-#hash-token-9037")]
    public void Env_values_with_quotes_and_inline_comments_register_only_the_value(string line, string value)
    {
        SecretPresentation.CollectSecrets(".env", line + "\n");
        Assert.DoesNotContain(value, SecretMasker.MaskText("output: " + value), StringComparison.Ordinal);
    }

    [Fact]
    public void Env_double_quoted_value_with_escaped_quote_registers_raw_and_unescaped_forms()
    {
        SecretPresentation.CollectSecrets(".env", "export SESSION=\"ab\\\"fake-sensitive-9038\" # note\n");
        Assert.DoesNotContain("fake-sensitive-9038", SecretMasker.MaskText("output: ab\\\"fake-sensitive-9038"), StringComparison.Ordinal);
        Assert.DoesNotContain("fake-sensitive-9038", SecretMasker.MaskText("output: ab\"fake-sensitive-9038"), StringComparison.Ordinal);
    }

    [Fact]
    public void Json_root_scalar_of_a_secret_file_is_collected_and_masked()
    {
        const string value = "fake-root-scalar-7781";
        SecretPresentation.CollectSecrets("auth.json", "\"" + value + "\"");
        Assert.DoesNotContain(value, SecretMasker.MaskText("output: " + value), StringComparison.Ordinal);
        SecretPresentation.ScreenShareMode = true;
        Assert.DoesNotContain(value, SecretPresentation.MaskDocument("auth.json", "\"" + value + "\""), StringComparison.Ordinal);
    }

    [Fact]
    public void Backup_restore_diff_follows_screen_share_mode()
    {
        var entry = new BackupEntry(new BackupSet("id", @"C:\fake\backups\1", DateTimeOffset.UtcNow, "save", []), new BackupFile(@"C:\fake\auth.json", "auth.json", 1, "x"));
        var text = "{\"access_token\":\"" + Secret + "\"}";
        var plain = new BackupRestoreViewModel(@"C:\fake\auth.json", [entry], "{}", _ => text);
        Assert.False(plain.CanReveal);
        Assert.Contains(plain.Lines, l => l.Text.Contains(Secret, StringComparison.Ordinal));

        SecretPresentation.ScreenShareMode = true;
        var shared = new BackupRestoreViewModel(@"C:\fake\auth.json", [entry], "{}", _ => text);
        Assert.True(shared.CanReveal);
        Assert.DoesNotContain(shared.Lines, l => l.Text.Contains(Secret, StringComparison.Ordinal));
        shared.RevealCommand.Execute(null);
        Assert.Contains(shared.Lines, l => l.Text.Contains(Secret, StringComparison.Ordinal));
    }

    [Fact]
    public void Reload_that_turns_the_file_into_a_secret_refreshes_the_masked_view()
    {
        SecretPresentation.ScreenShareMode = true;
        var document = Open("notes.txt", "plain words only");
        Assert.False(document.IsMaskedDisplay);
        var secret = TextFileReader.Decode(@"C:\fake\notes.txt", System.Text.Encoding.UTF8.GetBytes("plain words only"), DateTime.UtcNow)
            with { IsSecretFile = true };
        document.Buffer.Reload(secret);
        Assert.True(document.IsMaskedDisplay);
        Assert.Equal(SecretMasker.Mask, document.MaskedText);
    }

    [Fact]
    public void Text_document_shows_plain_text_by_default_and_masks_in_screen_share_mode()
    {
        var document = Open("auth.json", "{\"access_token\":\"" + Secret + "\"}");
        Assert.False(document.HasSecrets);
        Assert.False(document.IsMaskedDisplay);

        SecretPresentation.ScreenShareMode = true;
        document.OnScreenShareModeChanged();
        Assert.True(document.IsMaskedDisplay);
        Assert.DoesNotContain(Secret, document.MaskedText, StringComparison.Ordinal);

        document.RevealSecretsCommand.Execute(null);
        Assert.False(document.IsMaskedDisplay);

        // Switching again hides revealed views.
        document.OnScreenShareModeChanged();
        Assert.True(document.IsMaskedDisplay);

        SecretPresentation.ScreenShareMode = false;
        document.OnScreenShareModeChanged();
        Assert.False(document.IsMaskedDisplay);
        Assert.Contains(Secret, document.Buffer.Document.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_document_uses_the_link_target_secret_flag()
    {
        SecretPresentation.ScreenShareMode = true;
        var document = Open("notes.txt", "token " + Secret, isSecretFile: true);
        Assert.True(document.IsMaskedDisplay);
        Assert.Equal(SecretMasker.Mask, document.MaskedText);
    }

    [Fact]
    public void Diff_body_is_plain_by_default_and_hidden_with_an_eye_in_screen_share_mode()
    {
        var text = "{\"access_token\":\"" + Secret + "\"}";
        var plain = new DiffReviewItem("auth.json", TextDiffService.Compute("{}", text));
        Assert.False(plain.CanReveal);
        Assert.Contains(plain.Lines, l => l.Text.Contains(Secret, StringComparison.Ordinal));
        Assert.DoesNotContain(Secret, plain.Summary, StringComparison.Ordinal);

        SecretPresentation.ScreenShareMode = true;
        var shared = new DiffReviewItem("auth.json", TextDiffService.Compute("{}", text));
        Assert.True(shared.CanReveal);
        Assert.DoesNotContain(shared.Lines, l => l.Text.Contains(Secret, StringComparison.Ordinal));
        shared.RevealCommand.Execute(null);
        Assert.Contains(shared.Lines, l => l.Text.Contains(Secret, StringComparison.Ordinal));
    }

    [Fact]
    public void Environment_rows_mask_secret_names_only_in_screen_share_mode()
    {
        var row = new EnvironmentRow { Name = "API_KEY", Value = Secret };
        Assert.Equal(Secret, row.DisplayValue);

        SecretPresentation.ScreenShareMode = true;
        row.OnScreenShareModeChanged();
        Assert.Equal(SecretMasker.Mask, row.DisplayValue);
        row.IsRevealed = true;
        Assert.Equal(Secret, row.DisplayValue);
        row.OnScreenShareModeChanged();
        Assert.Equal(SecretMasker.Mask, row.DisplayValue);
    }

    private static TextDocumentViewModel Open(string name, string text, bool isSecretFile = false)
    {
        var snapshot = TextFileReader.Decode(System.IO.Path.Combine(@"C:\fake", name), System.Text.Encoding.UTF8.GetBytes(text), DateTime.UtcNow)
            with { IsSecretFile = isSecretFile };
        return new TextDocumentViewModel(new DocumentBuffer(snapshot), 1, isPreview: false, new NullHost(), _ => { });
    }

    private sealed class NullHost : ITextDocumentHost
    {
        public Task SaveAsync(TextDocumentViewModel document) => Task.CompletedTask;
        public void OpenNewView(TextDocumentViewModel document) { }
        public void SetEditorKind(TextDocumentViewModel document, EditorKind? kind) { }
        public void ShowFormHelp(ViewModels.Tools.RoleDescription? help) { }
    }
}
