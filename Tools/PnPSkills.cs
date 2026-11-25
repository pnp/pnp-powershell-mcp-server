#pragma warning disable MCPEXP005 // Skills extension is experimental in SDK 2.4.0.
using ModelContextProtocol.Extensions.Skills;

namespace PnPPowerShell.MCPServer.Tools;

/// <summary>Skills a host loads on demand, cut from the embedded guidance so there is no second copy.</summary>
internal static class PnPSkills
{
    internal const string ConnectName = "pnp-connect";

    /// <summary>The "## " sections of best-practices.md the connect skill carries, verbatim.</summary>
    internal static readonly string[] ConnectSections = ["Prerequisites", "Authentication Best Practices"];

    // Plain YAML scalar: no ": " and no " #" inside it.
    internal const string ConnectDescription =
        "Connect or sign in to SharePoint Online or Microsoft 365 with PnP PowerShell, including on a machine " +
        "that has never used it. Use before composing any Connect-PnPOnline, when a sign-in fails or hangs, " +
        "and before registering an Entra ID app.";

    // Names core tools only, so PNP_MCP_TOOLS cannot unpublish a step.
    private const string ConnectSteps =
        """
        # Connect to SharePoint with PnP PowerShell

        1. Call `pnp_get_connection_status`. If the session is already connected to the site the user wants, say so
           and stop. If it is connected to a different site, say which and ask whether to replace that connection.
           Stop on a no. On a yes, call `pnp_reset_session` first: the diagnosis reports a held connection as
           ready and gives no connect command.
        2. Call `pnp_diagnose_connection` with `targetUrl` set to the site. Do not compose a connect command yourself.
        3. If the report gives a connect command that needs no person, run it with `pnp_run_command`, not in a
           shell of your own: only that tool reaches this server's session.
        4. If signing in needs a browser or a device code, or something has to be installed or an app registered,
           do not run it: show the user the exact command and wait. Signing in from their own terminal does not
           connect this server's session. So when they say it is done, call `pnp_diagnose_connection` again with
           the same `targetUrl` and, if the command it now gives needs no person, run that with `pnp_run_command`.
        5. Call `pnp_get_connection_status` and say which site and account the session is connected as.

        The rest is this server's guidance on these steps, unchanged.
        """;

    internal static readonly Lazy<McpServerSkillPackage> Connect = new(() =>
        McpServerSkillPackage.Create([McpServerSkillFile.FromText(SkillsProtocol.SkillFileName, ConnectMarkdown())]));

    internal static IEnumerable<McpServerSkillPackage> All => [Connect.Value];

    /// <summary>Frontmatter, the steps, then the guidance sections. LF only, so the digest is the same on every platform.</summary>
    internal static string ConnectMarkdown() =>
        ($"---\nname: {ConnectName}\ndescription: {ConnectDescription}\n---\n\n{ConnectSteps}\n\n" +
         PnPPowerShellTools.ExtractSections(PnPPowerShellTools.BestPracticesDocument.Value, ConnectSections).TrimEnd() + "\n")
        .Replace("\r\n", "\n", StringComparison.Ordinal);
}
