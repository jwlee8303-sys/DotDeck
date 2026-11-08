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
