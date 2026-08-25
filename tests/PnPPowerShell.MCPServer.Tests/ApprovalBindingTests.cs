using ModelContextProtocol.Protocol;
using PnPPowerShell.MCPServer.Tools;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>Covers the requestState check that ties a destructive approval to the exact command shown.</summary>
public class ApprovalBindingTests
{
    private const string Command = "Remove-PnPTenantSite -Url https://contoso.sharepoint.com/sites/gone -Force";

    [Fact]
    public void An_approval_minted_for_the_command_is_accepted()
    {
        var state = PnPPowerShellTools.Fingerprint(Command);

        Assert.True(PnPPowerShellTools.IsApprovalBoundTo(state, Command));
    }

    [Fact]
    public void An_approval_for_a_different_command_is_rejected()
    {
        // The retry carrying different arguments must not arrive pre-approved.
        var state = PnPPowerShellTools.Fingerprint(Command);
        const string swapped = "Remove-PnPTenantSite -Url https://contoso.sharepoint.com/sites/production -Force";

        Assert.False(PnPPowerShellTools.IsApprovalBoundTo(state, swapped));
    }

    [Fact]
    public void A_single_character_change_is_rejected()
    {
        var state = PnPPowerShellTools.Fingerprint(Command);

        Assert.False(PnPPowerShellTools.IsApprovalBoundTo(state, Command + " "));
    }

    [Fact]
    public void A_missing_request_state_is_rejected()
    {
        // Fails closed: an approval that cannot be tied to what the user saw is not an approval.
        Assert.False(PnPPowerShellTools.IsApprovalBoundTo(null, Command));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    public void An_invalid_request_state_is_rejected(string state)
    {
        Assert.False(PnPPowerShellTools.IsApprovalBoundTo(state, Command));
    }

    [Fact]
    public void The_tool_exposes_no_parameter_that_approves_a_destructive_command()
    {
        var parameters = typeof(PnPPowerShellTools)
            .GetMethod(nameof(PnPPowerShellTools.RunPnpCommand))!
            .GetParameters()
            .Select(p => p.Name);

        Assert.DoesNotContain("confirmDestructive", parameters);
    }

    [Fact]
    public void An_approval_the_caller_computed_for_itself_is_rejected()
    {
        var unkeyed = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Command)));

        Assert.NotEqual(unkeyed, PnPPowerShellTools.Fingerprint(Command));
        Assert.False(PnPPowerShellTools.IsApprovalBoundTo(unkeyed, Command));
    }

    private static ElicitResult Ticked() => new()
    {
        Action = "accept",
        Content = new Dictionary<string, JsonElement> { ["confirm"] = JsonDocument.Parse("true").RootElement },
    };

    [Fact]
    public void A_fully_accepted_approval_bound_to_the_command_runs()
    {
        Assert.Null(PnPPowerShellTools.EvaluateApproval(PnPPowerShellTools.Fingerprint(Command), Command, Ticked(), "Remove-PnPTenantSite"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    public void An_accepted_approval_without_a_matching_request_state_does_not_run(string? requestState)
    {
        var refusal = PnPPowerShellTools.EvaluateApproval(requestState, Command, Ticked(), "Remove-PnPTenantSite");

        Assert.NotNull(refusal);
        Assert.StartsWith("Cancelled:", refusal);
    }

    [Fact]
    public void An_approval_minted_for_a_different_command_does_not_run()
    {
        var refusal = PnPPowerShellTools.EvaluateApproval(
            PnPPowerShellTools.Fingerprint("Remove-PnPTenantSite -Url https://contoso.sharepoint.com/sites/other -Force"),
            Command,
            Ticked(),
            "Remove-PnPTenantSite");

        Assert.NotNull(refusal);
    }

    [Fact]
    public void An_accepted_response_with_the_box_unticked_does_not_run()
    {
        var untickedBox = new ElicitResult { Action = "accept", Content = new Dictionary<string, JsonElement>() };

        var refusal = PnPPowerShellTools.EvaluateApproval(PnPPowerShellTools.Fingerprint(Command), Command, untickedBox, "Remove-PnPTenantSite");

        Assert.NotNull(refusal);
    }

    [Fact]
    public void A_declined_prompt_does_not_run()
    {
        var declined = new ElicitResult { Action = "decline" };

        Assert.NotNull(PnPPowerShellTools.EvaluateApproval(PnPPowerShellTools.Fingerprint(Command), Command, declined, "Remove-PnPTenantSite"));
    }

    [Fact]
    public void The_fingerprint_is_stable_across_calls()
    {
        Assert.Equal(PnPPowerShellTools.Fingerprint(Command), PnPPowerShellTools.Fingerprint(Command));
    }

    [Fact]
    public void The_fingerprint_is_case_sensitive_about_the_command()
    {
        Assert.NotEqual(
            PnPPowerShellTools.Fingerprint("Remove-PnPList -Identity Docs"),
            PnPPowerShellTools.Fingerprint("remove-pnplist -identity docs"));
    }

    [Fact]
    public void The_fingerprint_is_a_bounded_hash_rather_than_the_command_text()
    {
        var hugeCommand = "Remove-PnPListItem -List Documents -Identity 1 # " + new string('x', 100_000);

        var state = PnPPowerShellTools.Fingerprint(hugeCommand);

        Assert.Equal(64, state.Length);
        Assert.DoesNotContain("Remove-PnPListItem", state);
    }
}
