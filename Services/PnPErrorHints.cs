namespace PnPPowerShell.MCPServer.Services;

/// <summary>Turns a raw PnP PowerShell failure into a next action.</summary>
internal static class PnPErrorHints
{
    // First match wins over a substring scan, so this list runs specific to generic: environment,
    // then connection state, then Entra ID codes, then SharePoint messages, then PowerShell binding,
    // and only then bare HTTP status codes, which appear incidentally inside longer payloads.
    internal static readonly (string Match, string Hint)[] Hints =
    [
        ("Could not launch 'pwsh'",
            "PowerShell 7 is not installed or not on PATH. Install it from https://aka.ms/powershell, then restart your MCP client so it inherits the new PATH. Run pnp_diagnose_connection to confirm."),

        ("The PnP.PowerShell module is not installed",
            "Run: Install-Module -Name PnP.PowerShell -Scope CurrentUser -Force. Then run pnp_diagnose_connection to confirm the module is visible to this server."),

        ("The PowerShell session ended unexpectedly",
            "The session died and any PnP connection with it. Run pnp_diagnose_connection to check the environment, then reconnect with Connect-PnPOnline."),

        ("You are not signed in",
            "Run Connect-PnPOnline first. Check pnp_diagnose_connection to see whether this session already has a connection and what it is missing."),

        ("current connection holds no SharePoint context",
            "The connection carries no site URL, so a site-scoped cmdlet has nothing to target. Reconnect with Connect-PnPOnline -Url pointing at a SharePoint site. PnP acquires a SharePoint token on demand, so this is a missing URL rather than a missing scope."),

        ("do not support automatically switching context to the tenant administration site",
            "A device-login connection cannot be elevated to the admin site the way other auth methods can. Reconnect straight to it: Connect-PnPOnline -Url https://<tenant>-admin.sharepoint.com -DeviceLogin."),

        ("Tenant admin site",
            "This cmdlet only works against the tenant admin site. Reconnect to https://<tenant>-admin.sharepoint.com."),

        ("AADSTS65001",
            "The app has not been consented for this tenant. An administrator needs to grant consent before this will work. Register-PnPEntraIDAppForInteractiveLogin creates an app and prompts for that consent."),

        ("AADSTS65004",
            "Consent was declined at the sign-in prompt. Run the connect command again and accept, or ask an administrator to grant tenant-wide consent for the app."),

        ("AADSTS700016",
            "The client ID is not registered in this tenant. Confirm the -ClientId and -Tenant values. A newly created app registration can also take a few minutes to propagate, so retry once before changing anything."),

        ("AADSTS7000215",
            "The client secret is wrong or expired. Issue a new secret in the app registration, or switch to certificate authentication for unattended runs."),

        ("AADSTS50076",
            "MFA is required. Use Connect-PnPOnline -Interactive, or certificate/managed-identity auth for unattended runs."),

        ("AADSTS50011",
            "The redirect URI does not match the app registration. Interactive login needs http://localhost registered as a Mobile and desktop / public client redirect URI. Register-PnPEntraIDAppForInteractiveLogin sets this up correctly."),

        ("AADSTS50020",
            "The account signing in belongs to a different tenant, or is a guest without access. Sign in with an account in the target tenant, and pass -Tenant <tenant>.onmicrosoft.com explicitly."),

        ("AADSTS90002",
            "The tenant was not found. Check the -Tenant value: it must be the tenant's domain (contoso.onmicrosoft.com) or its directory ID, not the SharePoint URL."),

        ("AADSTS500011",
            "The requested resource has no service principal in this tenant, which usually means the app was never consented there. Grant admin consent, or run Register-PnPEntraIDAppForInteractiveLogin against this tenant."),

        ("AADSTS53003",
            "A Conditional Access policy blocked the sign-in. This is a tenant policy decision, not a bad credential; an administrator has to allow the app, the device or the location."),

        ("AADSTS650057",
            "The app registration is missing the permission being requested. Add the SharePoint/Graph permission to the app and grant admin consent."),

        ("Authorization_RequestDenied",
            "The signed-in account lacks the directory role needed for this operation. Creating an app registration needs Application Administrator or Global Administrator; ask an administrator to run Register-PnPEntraIDAppForInteractiveLogin for you."),

        ("Insufficient privileges to complete the operation",
            "The account or app is missing a directory permission. For app-only auth, add the required Graph application permission and grant admin consent; for delegated auth, the signing-in account needs the matching role."),

        ("AADSTS",
            "Entra ID rejected the sign-in. Look the AADSTS code up at https://login.microsoftonline.com/error, and run pnp_diagnose_connection to confirm what this session is currently connected as."),

        ("Attempted to perform an unauthorized operation",
            "Almost always a missing permission scope on the app registration rather than a wrong credential. Check the app's SharePoint/Graph application permissions and that admin consent was granted."),

        ("Cannot contact web site",
            "The site URL is wrong, or the site is locked or deleted. Confirm it with Get-PnPTenantSite."),

        ("File Not Found",
            "The server-relative path is probably wrong. List the parent folder first to confirm the exact name."),

        ("does not exist or you do not have permissions",
            "SharePoint returns the same error for 'missing' and 'no access'. Verify the name and that the connected account can see it."),

        ("is not recognized as a name of a cmdlet",
            "The cmdlet name is wrong or is not part of PnP.PowerShell. Find the right one with pnp_search_commands."),

        ("A parameter cannot be found that matches parameter name",
            "That parameter does not exist on this cmdlet. Check the exact parameter set with pnp_get_command_docs."),

        ("Cannot bind argument to parameter",
            "A required value was empty or the wrong type. Assign the result to a variable and inspect it before piping it onward."),

        ("The remote name could not be resolved",
            "DNS or network failure reaching Microsoft 365. Check connectivity and any proxy settings."),

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
    ];

    /// <summary>Appends a likely cause when the output is a failure; returns it unchanged otherwise.</summary>
    public static string Enrich(string output) => output + HintFor(output);

    /// <summary>The trailing hint block for a failure, or null when there is nothing to add.</summary>
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

    private static bool IsFailure(string output) =>
        output.StartsWith("Error:", StringComparison.OrdinalIgnoreCase);
}
