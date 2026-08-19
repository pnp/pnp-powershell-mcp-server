using PnPPowerShell.MCPServer.Services;
using PnPPowerShell.MCPServer.Tools;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>Covers what gets flagged for confirmation, including when the parse is unavailable.</summary>
public class ConfirmationTargetTests
{
    private static ScriptAnalysis Reads() =>
        new() { Commands = [new ScriptCommand { Name = "Get-PnPWeb", Verb = "Get" }] };

    [Fact]
    public void An_unanalysable_command_is_flagged()
    {
        using var _ = new EnvVar("PNP_MCP_READONLY", null);

        // Fails closed: the textual check cannot see an alias, an indirect invocation or a CSOM call,
        // so proceeding on it alone would let exactly those through unconfirmed.
        var flagged = PnPPowerShellTools.DetermineConfirmationTarget(null, "Get-PnPWeb");

        Assert.NotNull(flagged);
        Assert.Contains("could not be analysed", flagged);
    }

    [Theory]
    [InlineData("& $someCommand -Url https://x")]
    [InlineData("rm -Force C:\\data")]
    [InlineData("$l.DeleteObject(); $ctx.ExecuteQuery()")]
    public void The_cases_the_parser_exists_to_catch_are_flagged_without_a_parse(string command)
    {
        using var _ = new EnvVar("PNP_MCP_READONLY", null);

        // None of these match the destructive-verb regex, so before failing closed they ran unconfirmed.
        Assert.NotNull(PnPPowerShellTools.DetermineConfirmationTarget(null, command));
    }

    [Fact]
    public void A_plain_read_is_not_flagged_when_it_parsed()
    {
        using var _ = new EnvVar("PNP_MCP_READONLY", null);

        Assert.Null(PnPPowerShellTools.DetermineConfirmationTarget(Reads(), "Get-PnPWeb"));
    }

    [Fact]
    public void A_parsed_destructive_command_is_flagged_by_name()
    {
        using var _ = new EnvVar("PNP_MCP_READONLY", null);

        var analysis = new ScriptAnalysis
        {
            Commands = [new ScriptCommand { Name = "Remove-PnPTenantSite", Verb = "Remove" }],
        };

        Assert.Equal("Remove-PnPTenantSite", PnPPowerShellTools.DetermineConfirmationTarget(analysis, "Remove-PnPTenantSite -Url https://x"));
    }

    [Fact]
    public void A_destructive_name_used_only_as_an_argument_is_caught_textually()
    {
        using var _ = new EnvVar("PNP_MCP_READONLY", null);

        // The alias is defined after the parse, so the AST never sees it as an invocation.
        var flagged = PnPPowerShellTools.DetermineConfirmationTarget(
            Reads(),
            "Set-Alias nuke Remove-PnPTenantSite; nuke -Url https://x");

        Assert.Equal("Remove-PnPTenantSite", flagged);
    }

    [Fact]
    public void Nothing_is_flagged_in_read_only_mode()
    {
        using var _ = new EnvVar("PNP_MCP_READONLY", "true");

        // Read-only already refused anything that could change the tenant, so a prompt would be noise
        // and the textual check's false positives must not leak in.
        Assert.Null(PnPPowerShellTools.DetermineConfirmationTarget(Reads(), "\"mentions Remove-Item\"; Get-PnPWeb"));
    }

    [Fact]
    public void Read_only_mode_does_not_prompt_for_an_unanalysable_command_either()
    {
        using var _ = new EnvVar("PNP_MCP_READONLY", "true");

        // Enforce already blocked it before this point, so there is nothing left to confirm.
        Assert.Null(PnPPowerShellTools.DetermineConfirmationTarget(null, "& $someCommand"));
    }
}
