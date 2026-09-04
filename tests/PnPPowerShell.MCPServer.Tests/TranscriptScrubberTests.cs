using PnPPowerShell.MCPServer.Services;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>The scrubber is what stands between a recorded fixture and a committed tenant data leak.</summary>
public class TranscriptScrubberTests
{
    [Theory]
    [InlineData("https://acmecorp.sharepoint.com/sites/hr", "https://contoso.sharepoint.com/sites/hr")]
    [InlineData("https://acmecorp-admin.sharepoint.com", "https://contoso-admin.sharepoint.com")]
    [InlineData("https://acmecorp-my.sharepoint.com/personal/x", "https://contoso-my.sharepoint.com/personal/x")]
    [InlineData("acmecorp.onmicrosoft.com", "contoso.onmicrosoft.com")]
    [InlineData("https://acmecorp.sharepoint.us/sites/gov", "https://contoso.sharepoint.us/sites/gov")]
    public void Replaces_the_tenant_but_keeps_the_shape_of_the_host(string input, string expected) =>
        Assert.Equal(expected, new TranscriptScrubber().Scrub(input));

    [Fact]
    public void Maps_one_tenant_to_one_placeholder_everywhere_it_appears()
    {
        var scrubbed = new TranscriptScrubber().Scrub(
            "https://acmecorp.sharepoint.com/sites/a and https://acmecorp-admin.sharepoint.com and acmecorp.onmicrosoft.com");

        Assert.DoesNotContain("acmecorp", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, scrubbed.Split("contoso").Length - 1);
    }

    [Fact]
    public void Gives_a_second_tenant_a_second_placeholder()
    {
        var scrubbed = new TranscriptScrubber().Scrub("https://acmecorp.sharepoint.com https://globex.sharepoint.com");

        Assert.Contains("contoso.sharepoint.com", scrubbed, StringComparison.Ordinal);
        Assert.Contains("fabrikam.sharepoint.com", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void Catches_the_bare_tenant_name_once_a_url_has_named_it()
    {
        // The tenant token in a title is scrubbed only once a URL has named it.
        var scrubbed = new TranscriptScrubber().Scrub(
            """{"Url":"https://acmecorp.sharepoint.com/sites/team","Title":"Acmecorp Team Site"}""");

        Assert.DoesNotContain("Acmecorp", scrubbed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Leaves_its_own_placeholders_alone_when_run_again()
    {
        var scrubber = new TranscriptScrubber();
        var once = scrubber.Scrub("https://acmecorp.sharepoint.com/sites/hr admin@acmecorp.onmicrosoft.com");

        Assert.Equal(once, new TranscriptScrubber().Scrub(once));
    }

    [Theory]
    [InlineData("admin@acmecorp.onmicrosoft.com")]
    [InlineData("firstname.lastname@acmecorp.com")]
    [InlineData("i:0#.f|membership|admin@acmecorp.onmicrosoft.com")]
    public void Replaces_identities(string input)
    {
        var scrubbed = new TranscriptScrubber().Scrub(input);

        Assert.DoesNotContain("admin@", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lastname", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("user1@contoso.onmicrosoft.com", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void Gives_the_same_identity_the_same_placeholder_twice()
    {
        var scrubbed = new TranscriptScrubber().Scrub("a@x.com then b@x.com then a@x.com");

        Assert.Equal(2, scrubbed.Split("user1@").Length - 1);
        Assert.Contains("user2@", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void Replaces_guids_consistently()
    {
        var scrubbed = new TranscriptScrubber().Scrub(
            "3f2504e0-4f89-11d3-9a0c-0305e82c3301 and 3f2504e0-4f89-11d3-9a0c-0305e82c3301 and 11111111-2222-3333-4444-555555555555");

        Assert.DoesNotContain("3f2504e0", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, scrubbed.Split("00000000-0000-4000-8000-000000000001").Length - 1);
        Assert.Contains("00000000-0000-4000-8000-000000000002", scrubbed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("eyJ0eXAiOiJKV1QiLCJhbGciOiJSUzI1NiJ9.eyJhdWQiOiJodHRwcyJ9.aBcDeFgHiJkLmNoP", "[redacted-token]")]
    [InlineData("Authorization: Bearer abcdefghijklmnopqrstuvwxyz0123456789", "Bearer [redacted-token]")]
    [InlineData("-ClientSecret 'Q~7abcDEF.ghiJKL-mnoPQR_stu'", "[redacted-secret]")]
    [InlineData("-Password $env:PNP_PASSWORD", "[redacted-secret]")]
    [InlineData("-CertificateBase64Encoded 'MIIKrQIBAzCCCmkGCSqGSIb3'", "[redacted-secret]")]
    [InlineData("Thumbprint: 1A2B3C4D5E6F7A8B9C0D1E2F3A4B5C6D7E8F9A0B", "[redacted-thumbprint]")]
    public void Redacts_secrets(string input, string expected)
    {
        var scrubbed = new TranscriptScrubber().Scrub(input);

        Assert.Contains(expected, scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void Redacts_a_certificate_block_without_keeping_its_body()
    {
        var scrubbed = new TranscriptScrubber().Scrub(
            "-----BEGIN PRIVATE KEY-----\nMIIEvQIBADANBgkqhkiG9w0BAQEFAASC\nAAoCggEBAL\n-----END PRIVATE KEY-----");

        Assert.Contains("[redacted-certificate]", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("MIIEvQIBADANBgkqhkiG9w0BAQEFAASC", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void Leaves_ordinary_output_intact()
    {
        const string harmless = """{"Title":"Documents","ItemCount":42,"BaseTemplate":101,"Hidden":false}""";

        Assert.Equal(harmless, new TranscriptScrubber().Scrub(harmless));
    }

    [Fact]
    public void Handles_empty_input()
    {
        Assert.Equal(string.Empty, new TranscriptScrubber().Scrub(null));
        Assert.Equal(string.Empty, new TranscriptScrubber().Scrub(string.Empty));
    }

    [Theory]
    [InlineData("-ClientSecret:'Q~7abcDEFghiJKL'")]
    [InlineData("-ClientSecret : 'Q~7abcDEFghiJKL'")]
    [InlineData("-CertificatePassword:\"Q~7abcDEFghiJKL\"")]
    public void Redacts_a_secret_bound_with_the_colon_syntax(string input)
    {
        // PowerShell binds -Param:value as readily as -Param value, and the colon form once slipped through.
        var scrubbed = new TranscriptScrubber().Scrub(input);

        Assert.Contains("[redacted-secret]", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("Q~7abcDEF", scrubbed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\Users\gauta\certs\app.pfx")]
    [InlineData("/Users/gauta/report.csv")]
    [InlineData("/home/gauta/.pnp/cache")]
    public void Replaces_the_account_name_in_a_profile_path(string input)
    {
        var scrubbed = new TranscriptScrubber().Scrub(input);

        Assert.DoesNotContain("gauta", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("localuser", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void Gives_a_prefix_related_tenant_one_placeholder_not_two()
    {
        // "acme" replacing first turned "acmecorp" into "contosocorp" while its own URL said "fabrikam".
        var scrubbed = new TranscriptScrubber().Scrub(
            "https://acme.sharepoint.com and https://acmecorp.sharepoint.com and bare acmecorp");

        Assert.DoesNotContain("corp", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, scrubbed.Split("fabrikam").Length - 1);
    }

    [Fact]
    public void Keeps_the_empty_guid_which_means_none_rather_than_an_identity()
    {
        const string none = "00000000-0000-0000-0000-000000000000";

        Assert.Contains(none, new TranscriptScrubber().Scrub($$"""{"GroupId":"{{none}}"}"""), StringComparison.Ordinal);
    }

    [Fact]
    public void Redacts_the_app_display_name_the_connection_probe_records()
    {
        // Caught in a real recording: an app registration's name in someone's tenant.
        var scrubbed = new TranscriptScrubber().Scrub("""{"app":"Contoso Migration Tool","clientId":null}""");

        Assert.DoesNotContain("Contoso Migration Tool", scrubbed, StringComparison.Ordinal);
        Assert.Contains(@"""app"":""[redacted-app-name]""", scrubbed, StringComparison.Ordinal);

        // null means the token carried no app name.
        Assert.Contains("""{"app":null}""", new TranscriptScrubber().Scrub("""{"app":null}"""), StringComparison.Ordinal);

        // A quote inside the name must not end the match early.
        var escaped = new TranscriptScrubber().Scrub("""{"app":"My \"Cool\" App","x":1}""");

        Assert.DoesNotContain("Cool", escaped, StringComparison.Ordinal);
        Assert.Contains(@"""x"":1", escaped, StringComparison.Ordinal);
    }
}
