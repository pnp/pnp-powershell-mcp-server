using PnPPowerShell.MCPServer.Services;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>
/// After a parameter-binding failure, the hint names the nearest valid parameters from the corpus.
/// Nothing runs before execution: this only ever reads a failure PowerShell has already reported.
/// </summary>
public class ParameterSuggestionTests
{
    // Recorded shape: through Invoke-Expression, PowerShell names itself rather than the cmdlet.
    private const string UnknownIdentiy =
        "Error: Invoke-Expression: A parameter cannot be found that matches parameter name 'Identiy'.";

    [Fact]
    public void A_misspelled_parameter_on_a_known_cmdlet_gets_the_nearest_names()
    {
        var hint = PnPErrorHints.HintFor(UnknownIdentiy, "Get-PnPList -Identiy Documents");

        Assert.NotNull(hint);
        Assert.Contains("pnp_get_command_docs", hint);
        Assert.Contains("Get-PnPList has no -Identiy", hint);
        Assert.Contains("-Identity", hint);
    }

    [Fact]
    public void The_cmdlet_that_owns_the_flag_is_the_one_consulted_in_a_pipeline()
    {
        var hint = PnPErrorHints.HintFor(
            "Error: Invoke-Expression: A parameter cannot be found that matches parameter name 'Titel'.",
            "Get-PnPList -Identity Documents | Set-PnPList -Titel Docs");

        Assert.NotNull(hint);
        Assert.Contains("Set-PnPList has no -Titel", hint);
        Assert.Contains("-Title", hint);
        Assert.DoesNotContain("Get-PnPList has", hint);
    }

    [Fact]
    public void A_superseded_alias_is_followed_to_the_current_cmdlet()
    {
        var hint = PnPErrorHints.HintFor(
            "Error: Invoke-Expression: A parameter cannot be found that matches parameter name 'Identiy'.",
            "Add-PnPAzureADGroupMember -Identiy sales -Users a@contoso.com");

        Assert.NotNull(hint);
        Assert.Contains("Add-PnPEntraIDGroupMember has no -Identiy", hint);
        Assert.Contains("-Identity", hint);
    }

    [Fact]
    public void A_bind_failure_reports_the_type_the_parameter_takes()
    {
        var hint = PnPErrorHints.HintFor(
            "Error: Invoke-Expression: Cannot bind argument to parameter 'Identity' because it is null.",
            "Get-PnPList -Identity $missing");

        Assert.NotNull(hint);
        Assert.Contains("inspect it", hint);
        Assert.Contains("-Identity on Get-PnPList takes ListPipeBind", hint);
    }

    [Fact]
    public void A_parameter_the_corpus_knows_but_the_module_rejected_points_at_the_version_gap()
    {
        var hint = PnPErrorHints.HintFor(
            "Error: Invoke-Expression: A parameter cannot be found that matches parameter name 'Includes'.",
            "Get-PnPList -Includes Fields");

        Assert.NotNull(hint);
        Assert.Contains("installed module may be older", hint);
        Assert.Contains(CommandCorpus.ModuleVersion!, hint);
    }

    [Fact]
    public void An_unknown_cmdlet_degrades_to_the_existing_hint_unchanged()
    {
        var bare = PnPErrorHints.HintFor(UnknownIdentiy);
        var withCommand = PnPErrorHints.HintFor(UnknownIdentiy, "Get-PnPNoSuchThing -Identiy x");

        Assert.NotNull(bare);
        Assert.Equal(bare, withCommand);
    }

    [Fact]
    public void Two_cmdlets_and_no_visible_flag_yields_no_guess()
    {
        var bare = PnPErrorHints.HintFor(UnknownIdentiy);
        var splatted = PnPErrorHints.HintFor(UnknownIdentiy, "Get-PnPWeb | Get-PnPList @args");

        Assert.Equal(bare, splatted);
    }

    [Fact]
    public void Nothing_resembling_the_name_is_said_plainly_rather_than_guessed()
    {
        var hint = PnPErrorHints.HintFor(
            "Error: Invoke-Expression: A parameter cannot be found that matches parameter name 'Zzz'.",
            "Get-PnPList -Zzz 1");

        Assert.NotNull(hint);
        Assert.Contains("no parameter resembling -Zzz", hint);
        Assert.DoesNotContain("nearest:", hint);
    }

    [Theory]
    [InlineData("Error: The remote server returned an error: (401) Unauthorized")]
    [InlineData("Error: The remote server returned an error: (404) Not Found")]
    [InlineData("Error: The remote server returned an error: (429) Too Many Requests")]
    [InlineData("Error: You are not signed in")]
    public void An_unrelated_failure_is_untouched_by_the_command_text(string error)
    {
        Assert.Equal(PnPErrorHints.HintFor(error), PnPErrorHints.HintFor(error, "Get-PnPList -Identity Documents"));
    }

    [Fact]
    public void A_successful_command_gets_no_suggestion()
    {
        const string success = "[{\"Title\":\"Documents\"}]";

        Assert.Null(PnPErrorHints.HintFor(success, "Get-PnPList -Identiy Documents"));
        Assert.Equal(success, PnPErrorHints.Enrich(success, "Get-PnPList -Identiy Documents"));
    }
}
