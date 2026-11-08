using DotDeck.App.ViewModels.Dialogs;
using DotDeck.Core.Documents;
using DotDeck.Core.Security;

namespace DotDeck.App.Tests;

public sealed class SecretPresentationTests
{
    [Fact]
    public void Auth_and_toml_are_masked_without_changing_original_and_diff_can_be_revealed()
    {
        const string secret = "fake-private-credential-9374";
        var text = "{\"access_token\":\"" + secret + "\"}";
        var masked = SecretPresentation.MaskDocument("auth.json", text);
        Assert.DoesNotContain(secret, masked, StringComparison.Ordinal);
        Assert.Contains(secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, SecretMasker.MaskText("output: " + secret), StringComparison.Ordinal);
        var diff = new DiffReviewItem("auth.json", TextDiffService.Compute("{}", text));
        Assert.DoesNotContain(diff.Lines, l => l.Text.Contains(secret, StringComparison.Ordinal));
        diff.RevealCommand.Execute(null);
        Assert.Contains(diff.Lines, l => l.Text.Contains(secret, StringComparison.Ordinal));
        var toml = "[mcp_servers.test.env]\nAPI_KEY = '" + secret + "'\n";
        Assert.DoesNotContain(secret, SecretPresentation.MaskDocument("config.toml", toml), StringComparison.Ordinal);
    }
}
