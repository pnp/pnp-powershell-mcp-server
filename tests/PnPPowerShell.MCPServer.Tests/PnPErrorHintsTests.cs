using PnPPowerShell.MCPServer.Services;

namespace PnPPowerShell.MCPServer.Tests;

public class PnPErrorHintsTests
{
    [Theory]
    [InlineData("You are not signed in", "Connect-PnPOnline")]
    [InlineData("The remote server returned an error: (401) Unauthorized", "expired")]
    [InlineData("The remote server returned an error: (403) Forbidden", "not authorized")]
    [InlineData("The remote server returned an error: (429) Too Many Requests", "Throttled")]
    [InlineData("The remote server returned an error: (404) Not Found", "URL is exact")]
    [InlineData("Attempted to perform an unauthorized operation", "permission scope")]
    [InlineData("AADSTS65001: The user or administrator has not consented", "consent")]
    [InlineData("AADSTS50076: due to a configuration change", "MFA")]
    [InlineData("Cannot contact web site", "locked or deleted")]
    [InlineData("A parameter cannot be found that matches parameter name 'Foo'", "pnp_get_command_docs")]
    public void Enrich_appends_a_cause_for_a_known_failure(string error, string expectedHint)
    {
        var result = PnPErrorHints.Enrich("Error: " + error);

        Assert.Contains("Likely cause:", result);
        Assert.Contains(expectedHint, result);
    }

    [Fact]
    public void Enrich_prefers_the_specific_rule_over_a_generic_one()
    {
        // A 403 whose text also echoes the admin URL must report the authorization cause, not a
        // guess about the admin site.
        var result = PnPErrorHints.Enrich(
            "Error: The remote server returned an error: (403) Forbidden. Url: https://contoso-admin.sharepoint.com");

        Assert.Contains("not authorized", result);
    }

    [Fact]
    public void Enrich_leaves_successful_output_untouched()
    {
        const string success = "{\"Title\":\"Documents\",\"ItemCount\":42}";

        Assert.Equal(success, PnPErrorHints.Enrich(success));
    }

    [Fact]
    public void Enrich_does_not_annotate_success_that_carries_a_warning_block()
    {
        // The session appends a Warnings block to successful output whenever stderr had content, so a
        // command that merely logged a throttle warning must not be reported as throttled.
        const string output = "{\"Title\":\"Documents\"}\n\nWarnings:\nThe remote server returned an error: (429)";

        Assert.Equal(output, PnPErrorHints.Enrich(output));
    }

    [Fact]
    public void Enrich_does_not_annotate_data_that_merely_mentions_an_error_code()
    {
        const string output = "{\"LastError\":\"(401) Unauthorized\",\"Site\":\"contoso\"}";

        Assert.DoesNotContain("Likely cause:", PnPErrorHints.Enrich(output));
    }

    [Fact]
    public void Enrich_leaves_an_unrecognised_failure_unchanged()
    {
        const string error = "Error: something entirely unfamiliar happened";

        Assert.Equal(error, PnPErrorHints.Enrich(error));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Enrich_handles_empty_output(string output)
    {
        Assert.Equal(output, PnPErrorHints.Enrich(output));
    }
}
