using ModelContextProtocol;
using ModelContextProtocol.Server;
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
        Connect to {Site(siteUrl)} with PnP PowerShell.

        1. Call pnp_get_connection_status. If this session is already connected to that site, say so and stop.
           If it is connected to a different site, tell me which, and ask whether to replace that connection.
           Stop if I say no. If I say yes, call pnp_reset_session first: the diagnosis reports an existing
           connection as ready and gives no connect command while one is held.
        2. Call pnp_diagnose_connection with targetUrl set to the site. Do not compose a Connect-PnPOnline from memory.
        3. If the report gives a connect command that needs no person, run it with pnp_run_command.
        4. If signing in needs a browser or a device code, do not run it: show me the exact command to run in my
           own PowerShell 7 terminal, and wait for me to say it is done. Signing in there does not connect this
           server's session. So when I say it is done, call pnp_diagnose_connection again with the same targetUrl
           and, if the connect command it now reports needs no person, run that with pnp_run_command.
        5. If the report says an app registration is needed, ask me before proposing one, and tell me what it
           would be granted by default.
        6. Call pnp_get_connection_status and tell me which site and which account this session is connected as.
        """;

    [McpServerPrompt(Name = "diagnose", Title = "Check the PnP PowerShell setup")]
    [Description("Checks whether this machine is ready to run PnP PowerShell and reports what is missing, without changing anything.")]
    public static string Diagnose(
        [Description("Optional: the site you want to work against, so the report names the exact connect command")] string? siteUrl = null) =>
        $"""
        Check whether this machine is ready to run PnP PowerShell{(string.IsNullOrWhiteSpace(siteUrl) ? string.Empty : $" against {Site(siteUrl)}")}.

        Call pnp_diagnose_connection{(string.IsNullOrWhiteSpace(siteUrl) ? string.Empty : " with targetUrl set to that site")}. Then tell me, in order:
        - what is already working;
        - each check that failed, its cause, and the exact command that fixes it;
        - how I would sign in from here.

        Do not install anything, register an app or sign in as part of this. Show me the commands and let me decide.
        """;

    // Refused, not shortened: a cut URL is a different site, and anything else would land inside an instruction.
    private static string Site(string? value) =>
        PnPPowerShellTools.IsSiteUrl(value)
            ? $"'{value!.Trim()}'"
            : throw new McpException(
                $"siteUrl must be a SharePoint site URL of at most {PnPPowerShellTools.MaxSiteUrlChars} characters, " +
                "such as https://contoso.sharepoint.com/sites/marketing.");
}
