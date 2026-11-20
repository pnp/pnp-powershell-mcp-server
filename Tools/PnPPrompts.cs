using ModelContextProtocol.Server;
using PnPPowerShell.MCPServer.Services;
using System.ComponentModel;

namespace PnPPowerShell.MCPServer.Tools;

/// <summary>Playbooks a client lists as slash commands. Only the two that need no guessing: getting connected.</summary>
[McpServerPromptType]
internal sealed class PnPPrompts
{
    [McpServerPrompt(Name = "connect", Title = "Connect to a SharePoint site")]
    [Description("Signs this conversation in to a SharePoint site, using whatever this machine can actually authenticate with.")]
    public static string Connect(
        [Description("The site to connect to, e.g. https://contoso.sharepoint.com/sites/marketing")] string siteUrl) =>
        $"""
        Connect to {Quote(siteUrl)} with PnP PowerShell.

        1. Call pnp_get_connection_status. If this session is already connected to that site, say so and stop.
        2. Call pnp_diagnose_connection with targetUrl set to the site. Do not compose a Connect-PnPOnline from memory.
        3. If the report gives a connect command that needs no person, run it with pnp_run_command.
           If signing in needs a browser or a device code, do not run it: show me the exact command to run in my
           own PowerShell 7 terminal, and wait for me to say it is done.
        4. If the report says an app registration is needed, ask me before proposing one, and tell me what it
           would be granted by default.
        5. Call pnp_get_connection_status and tell me which site and which account this session is connected as.
        """;

    [McpServerPrompt(Name = "diagnose", Title = "Check the PnP PowerShell setup")]
    [Description("Checks whether this machine is ready to run PnP PowerShell and reports what is missing, without changing anything.")]
    public static string Diagnose(
        [Description("Optional: the site you want to work against, so the report names the exact connect command")] string? siteUrl = null) =>
        $"""
        Check whether this machine is ready to run PnP PowerShell{(string.IsNullOrWhiteSpace(siteUrl) ? string.Empty : $" against {Quote(siteUrl)}")}.

        Call pnp_diagnose_connection{(string.IsNullOrWhiteSpace(siteUrl) ? string.Empty : " with targetUrl set to that site")}. Then tell me, in order:
        - what is already working;
        - each check that failed, its cause, and the exact command that fixes it;
        - how I would sign in from here.

        Do not install anything, register an app or sign in as part of this. Show me the commands and let me decide.
        """;

    // The user's own text, but it lands inside an instruction, so it is kept to one bounded line.
    private static string Quote(string? value) => $"'{OutputLimit.Clamp(OutputLimit.Echo(value?.Trim()), 512)}'";
}
