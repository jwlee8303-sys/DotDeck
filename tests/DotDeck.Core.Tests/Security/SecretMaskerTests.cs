using DotDeck.Core.Security;

namespace DotDeck.Core.Tests.Security;

public class SecretMaskerTests
{
    [Theory]
    [InlineData("OPENAI_API_KEY", true)]
    [InlineData("github_token", true)]
    [InlineData("ClientSecret", true)]
    [InlineData("DB_PASSWORD", true)]
    [InlineData("MYSQL_PWD", true)]
    [InlineData("AZURE_CREDENTIALS", true)]
    [InlineData("PATH", false)]
    [InlineData("USERPROFILE", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Detects_secret_names(string? name, bool expected)
    {
        Assert.Equal(expected, SecretMasker.IsSecretName(name));
    }

    [Fact]
    public void MaskValue_masks_only_secret_names_with_fixed_length()
    {
        Assert.Equal(SecretMasker.Mask, SecretMasker.MaskValue("API_KEY", "a"));
        Assert.Equal(SecretMasker.Mask, SecretMasker.MaskValue("API_KEY", new string('x', 200)));
        Assert.Equal("C:\\bin", SecretMasker.MaskValue("PATH", "C:\\bin"));
        Assert.Equal(string.Empty, SecretMasker.MaskValue("API_KEY", string.Empty));
    }

    [Theory]
    [InlineData("set API_KEY=sk-123 done", "set API_KEY=" + SecretMasker.Mask + " done")]
    [InlineData("{\"github_token\": \"ghp_abc\"}", "{\"github_token\": \"" + SecretMasker.Mask + "\"}")]
    [InlineData("password: 'hunter2'", "password: '" + SecretMasker.Mask + "'")]
    [InlineData("DB_PASSWORD = p@ss;next", "DB_PASSWORD = " + SecretMasker.Mask + ";next")]
    [InlineData("PATH=C:\\bin USER=me", "PATH=C:\\bin USER=me")]
    [InlineData("Saved file D:\\x\\keys.md", "Saved file D:\\x\\keys.md")]
    [InlineData("Exception: token=abc", "Exception: token=" + SecretMasker.Mask)]
    [InlineData("note=\"x\" api_key=\"y\"", "note=\"x\" api_key=\"" + SecretMasker.Mask + "\"")]
    [InlineData("{\"password\":\"abc'def\"}", "{\"password\":\"" + SecretMasker.Mask + "\"}")]
    [InlineData("{\"token\":\"abc\\\"def\"}", "{\"token\":\"" + SecretMasker.Mask + "\"}")]
    [InlineData("Authorization: Bearer abc.def-123", "Authorization: Bearer " + SecretMasker.Mask)]
    [InlineData("auth basic dXNlcjpwYXNz==", "auth basic " + SecretMasker.Mask)]
    [InlineData("GET /api?x=1&token=abc", "GET /api?x=1&token=" + SecretMasker.Mask)]
    [InlineData("TOKEN=\"abc\ndef\" next", "TOKEN=\"" + SecretMasker.Mask + "\" next")]
    [InlineData("TOKEN=\"abc def\nnext line", "TOKEN=" + SecretMasker.Mask + "\nnext line")]
    [InlineData("password: 'unterminated", "password: " + SecretMasker.Mask)]
    [InlineData("{\"token\":\"x\",\"user\":\"bob\"}", "{\"token\":\"" + SecretMasker.Mask + "\",\"user\":\"bob\"}")]
    public void MaskText_masks_secret_assignments(string input, string expected)
    {
        Assert.Equal(expected, SecretMasker.MaskText(input));
    }

    [Fact]
    public void MaskText_is_idempotent()
    {
        var once = SecretMasker.MaskText("TOKEN=abc \"secret\": \"xyz\"");

        Assert.Equal(once, SecretMasker.MaskText(once));
        Assert.DoesNotContain("abc", once, StringComparison.Ordinal);
        Assert.DoesNotContain("xyz", once, StringComparison.Ordinal);
    }

    [Fact]
    public void MaskText_scales_linearly_on_long_inputs()
    {
        var longWord = new string('a', 200_000);
        var quoted = string.Concat(Enumerable.Repeat("\"a", 50_000));
        var assignments = string.Concat(Enumerable.Repeat("\"a=", 22_000)) + "token=plain-secret";

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(longWord, SecretMasker.MaskText(longWord));
        Assert.Equal(quoted, SecretMasker.MaskText(quoted));
        var masked = SecretMasker.MaskText(assignments);
        sw.Stop();

        // Either masked correctly or redacted entirely; the secret must never survive.
        Assert.DoesNotContain("plain-secret", masked, StringComparison.Ordinal);
        Assert.True(sw.ElapsedMilliseconds < 3000, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void MaskText_handles_null_and_empty()
    {
        Assert.Equal(string.Empty, SecretMasker.MaskText(null));
        Assert.Equal(string.Empty, SecretMasker.MaskText(string.Empty));
    }
}
