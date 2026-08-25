using PnPPowerShell.MCPServer.Services;
using PnPPowerShell.MCPServer.Tools;

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
    [InlineData("Could not launch 'pwsh'. Install PowerShell 7.4 or above", "pnp_diagnose_connection")]
    [InlineData("The PnP.PowerShell module is not installed", "Install-Module -Name PnP.PowerShell")]
    [InlineData("AADSTS50011: The redirect URI specified does not match", "http://localhost")]
    [InlineData("Authorization_RequestDenied: Insufficient privileges", "Application Administrator")]
    [InlineData("AADSTS99999: something new Microsoft added", "login.microsoftonline.com/error")]
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
    public void A_bare_status_code_never_outranks_a_pattern_that_identifies_the_failure()
    {
        var statusCodes = new[] { "(401)", "(403)", "(404)", "(429)", "(503)" };
        var firstStatusCode = Array.FindIndex(PnPErrorHints.Hints, h => statusCodes.Contains(h.Match));
        var specific = PnPErrorHints.Hints[..firstStatusCode].Select(h => h.Match).ToList();

        Assert.Contains("does not exist or you do not have permissions", specific);
        Assert.Contains("AADSTS", specific);
        Assert.Contains("File Not Found", specific);
        Assert.All(
            PnPErrorHints.Hints[firstStatusCode..],
            h => Assert.Contains(h.Match, statusCodes));
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

    [Theory]
    [InlineData("Connect-PnPOnline -Url https://contoso.sharepoint.com -Interactive", true)]
    [InlineData("  connect-pnponline -url https://contoso.sharepoint.com  ", true)]
    [InlineData("Get-PnPListItem -List Big", false)]
    [InlineData("Disconnect-PnPOnline", false)]
    // Chaining is documented, and the chained work must keep the full budget.
    [InlineData("Connect-PnPOnline -Url https://contoso.sharepoint.com; Get-PnPTenantSite", false)]
    [InlineData("Connect-PnPOnline -Url https://contoso.sharepoint.com\nGet-PnPTenantSite", false)]
    // A backtick continues one statement; best-practices.md documents connects written this way.
    [InlineData("Connect-PnPOnline -Url https://contoso.sharepoint.com `\n  -ClientId abc -PersistLogin", true)]
    public void Only_a_command_that_does_nothing_but_sign_in_gets_the_sign_in_timeout(string command, bool expected) =>
        Assert.Equal(expected, PnPPowerShellTools.IsSignIn(command));

    [Fact]
    public void A_timed_out_sign_in_is_not_told_to_narrow_its_query()
    {
        Assert.DoesNotContain("PageSize", PnPPowerShellTools.SignInTimedOut);
        Assert.Contains("waiting for a person", PnPPowerShellTools.SignInTimedOut);
        Assert.Contains("pnp_diagnose_connection", PnPPowerShellTools.SignInTimedOut);
    }

    [Fact]
    public void The_no_app_registration_failure_is_explained_rather_than_echoed()
    {
        // Both halves of what PnP really says, recorded from a real tenant.
        foreach (var output in (string[])[
            "Error: Connect-PnPOnline: Specified method is not supported.\n\nOutput before the failure:\nWARNING: Please specify a valid client id for an Entra ID App Registration.",
            "Error: Connect-PnPOnline: Specified method is not supported."])
        {
            var hint = PnPErrorHints.HintFor(output);

            Assert.NotNull(hint);
            Assert.Contains("pnp_diagnose_connection", hint);
        }
    }

    [Theory]
    [InlineData("AADSTS50173", "revoked")]
    [InlineData("AADSTS700082", "expired through inactivity")]
    [InlineData("invalid_grant", "can no longer be exchanged")]
    public void A_revoked_or_expired_token_is_distinguished_from_a_misconfiguration(string code, string expected)
    {
        var hint = PnPErrorHints.HintFor($"Error: Connect-PnPOnline: {code}: something went wrong.");

        Assert.NotNull(hint);
        Assert.Contains(expected, hint);
    }

    [Fact]
    public void The_specific_token_codes_are_ordered_ahead_of_the_generic_AADSTS_catch_all()
    {
        var matches = PnPErrorHints.Hints.Select(h => h.Match).ToList();
        var generic = matches.IndexOf("AADSTS");

        Assert.True(generic >= 0, "The generic AADSTS entry is gone; this test guards its position.");

        foreach (var code in (string[])["AADSTS50173", "AADSTS700082", "AADSTS50058"])
        {
            Assert.True(
                matches.IndexOf(code) < generic,
                $"{code} must be listed before the generic AADSTS entry or first-match-wins hides it.");
        }
    }
}
