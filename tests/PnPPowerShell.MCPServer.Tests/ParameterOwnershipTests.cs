using PnPPowerShell.MCPServer.Services;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>
/// The only judgment in the suggestion path is which cmdlet owns the rejected flag. A wrong owner
/// produces a confident suggestion for the wrong cmdlet, so every ambiguity must resolve to the
/// right one or to no suggestion at all.
/// </summary>
public class ParameterOwnershipTests
{
    private static string Unknown(string name) =>
        $"Error: Invoke-Expression: A parameter cannot be found that matches parameter name '{name}'.";

    [Theory]
    // The flag text appears inside a string argument of an earlier cmdlet.
    [InlineData("Titel", "Get-PnPListItem -List \"Docs-Titel\" | Set-PnPList -Titel x", "Set-PnPList has no -Titel")]
    // The flag is a prefix of a valid flag on an earlier cmdlet.
    [InlineData("Fiel", "Get-PnPListItem -List Docs -Fields Title | Set-PnPList -Identity Docs -Fiel x", "Set-PnPList has no")]
    // A hyphenated value must not be mistaken for a command.
    [InlineData("Interactiv", "Connect-PnPOnline -Url https://contoso-admin.sharepoint.com -Interactiv", "Connect-PnPOnline has no -Interactiv; nearest: -Interactive")]
    // Inside a script block.
    [InlineData("Titel", "Get-PnPList | ForEach-Object { Set-PnPList -Identity $_ -Titel x }", "Set-PnPList has no -Titel")]
    // Inside a subexpression.
    [InlineData("Includs", "$w = (Get-PnPWeb -Includs Title)", "Get-PnPWeb has no -Includs; nearest: -Includes")]
    // Backtick continuation.
    [InlineData("Interactiv", "Connect-PnPOnline -Url https://contoso.sharepoint.com `\n  -Interactiv", "Connect-PnPOnline has no -Interactiv")]
    // Lower-case as typed.
    [InlineData("identiy", "get-pnplist -identiy docs", "Get-PnPList has no -identiy; nearest: -Identity")]
    // Colon syntax.
    [InlineData("Identiy", "Get-PnPList -Identiy:Docs", "Get-PnPList has no -Identiy")]
    public void The_cmdlet_that_owns_the_flag_is_named(string parameter, string command, string expected)
    {
        var hint = PnPErrorHints.HintFor(Unknown(parameter), command);

        Assert.NotNull(hint);
        Assert.Contains(expected, hint);
    }

    [Theory]
    // The flag belongs to a non-PnP cmdlet; the PnP cmdlet upstream must not be blamed.
    [InlineData("Proprety", "Get-PnPList | Select-Object -Proprety Title")]
    [InlineData("Proprety", "Get-PnPList | Where-Object -Proprety Title -EQ Docs | Set-PnPList -Hidden $true")]
    // Two PnP cmdlets and the flag is nowhere in the text.
    [InlineData("Identiy", "Get-PnPWeb | Get-PnPList @args")]
    public void An_unattributable_flag_yields_the_bare_hint(string parameter, string command)
    {
        Assert.Equal(PnPErrorHints.HintFor(Unknown(parameter)), PnPErrorHints.HintFor(Unknown(parameter), command));
    }
}
