namespace PnPPowerShell.MCPServer.Services;

/// <summary>Turns a raw PnP PowerShell failure into a next action.</summary>
internal static class PnPErrorHints
{
    // First match wins, so specific patterns come before generic ones.
    private static readonly (string Match, string Hint)[] Hints =
    [
        ("You are not signed in",
            "Run Connect-PnPOnline first. Check pnp_get_connection_status to see whether this session already has a connection."),

        ("current connection holds no SharePoint context",
            "The session is connected to Graph only. Reconnect with Connect-PnPOnline -Url pointing at a SharePoint site."),

        ("Tenant admin site",
            "This cmdlet only works against the tenant admin site. Reconnect to https://<tenant>-admin.sharepoint.com."),

        ("Attempted to perform an unauthorized operation",
            "Almost always a missing permission scope on the app registration rather than a wrong credential. Check the app's SharePoint/Graph application permissions and that admin consent was granted."),

        ("AADSTS65001",
            "The app has not been consented for this tenant. An administrator needs to grant consent before this will work."),

        ("AADSTS700016",
            "The client ID is not registered in this tenant. Confirm the -ClientId and -Tenant values."),

        ("AADSTS50076",
            "MFA is required. Use Connect-PnPOnline -Interactive, or certificate/managed-identity auth for unattended runs."),

        ("(401)",
            "The token is invalid or has expired. Reconnect with Connect-PnPOnline; if it keeps happening, clear the session with pnp_reset_session and connect again."),

        ("(403)",
            "Authenticated but not authorized. Confirm the account holds the required role (e.g. SharePoint Administrator) and, for app-only auth, that the required permission scopes are consented."),

        ("(429)",
            "Throttled by Microsoft 365. Wait before retrying, reduce -PageSize, and process fewer objects per call. Repeated tight loops make throttling worse."),

        ("(503)",
            "Microsoft 365 is temporarily unavailable or throttling hard. Retry after a pause rather than immediately."),

        ("(404)",
            "Check the URL is exact (including /sites/ or /teams/) and that the object has not already been deleted."),

        ("Cannot contact web site",
            "The site URL is wrong, or the site is locked or deleted. Confirm it with Get-PnPTenantSite."),

        ("File Not Found",
            "The server-relative path is probably wrong. List the parent folder first to confirm the exact name."),

        ("does not exist or you do not have permissions",
            "SharePoint returns the same error for 'missing' and 'no access'. Verify the name and that the connected account can see it."),

        ("The remote name could not be resolved",
            "DNS or network failure reaching Microsoft 365. Check connectivity and any proxy settings."),

        ("is not recognized as a name of a cmdlet",
            "The cmdlet name is wrong or is not part of PnP.PowerShell. Find the right one with pnp_search_commands."),

        ("A parameter cannot be found that matches parameter name",
            "That parameter does not exist on this cmdlet. Check the exact parameter set with pnp_get_command_docs."),

        ("Cannot bind argument to parameter",
            "A required value was empty or the wrong type. Assign the result to a variable and inspect it before piping it onward."),
    ];

    /// <summary>Appends a likely cause when the output is a failure; returns it unchanged otherwise.</summary>
    public static string Enrich(string output) => output + HintFor(output);

    /// <summary>The trailing hint block for a failure, or null when there is nothing to add.</summary>
    // Returned separately so a caller can reserve room for it inside the output cap instead of
    // appending it afterwards and overshooting.
    public static string? HintFor(string? output)
    {
        if (string.IsNullOrWhiteSpace(output) || !IsFailure(output))
        {
            return null;
        }

        foreach (var (match, hint) in Hints)
        {
            if (output.Contains(match, StringComparison.OrdinalIgnoreCase))
            {
                return $"\n\nLikely cause: {hint}";
            }
        }

        return null;
    }

    // Only the session's own failure prefix counts. Matching "Exception" or "Warnings:" would annotate
    // successful output, because a command that merely wrote to stderr still returns its data with a
    // Warnings block appended.
    private static bool IsFailure(string output) =>
        output.StartsWith("Error:", StringComparison.OrdinalIgnoreCase);
}
