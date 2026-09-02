using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PnPPowerShell.MCPServer.Services;

/// <summary>One entry in PnP's persisted-login store: a SharePoint host and the app it signs in with.</summary>
internal sealed class PersistedLogin
{
    public string? Url { get; set; }

    public string? ClientId { get; set; }

    public bool Enabled { get; set; }
}

/// <summary>The shape of PnP's settings file, so it can be read with the source-generated serializer.</summary>
internal sealed class PersistedLoginStore
{
    public List<PersistedLogin>? Cache { get; set; }
}

/// <summary>What this machine can authenticate with, read before any tenant is contacted.</summary>
internal sealed record AuthFacts(
    IReadOnlyList<PersistedLogin> PersistedLogins,
    bool TokenCachePresent,
    string? ClientIdVariable,
    string? ClientId,
    string? CertificatePath,
    string? StoreError)
{
    public static AuthFacts None { get; } = new([], false, null, null, null, null);

    /// <summary>The persisted login covering <paramref name="url"/>, or null when none does.</summary>
    // Tenant, not host: PnP resolves these per tenant, admin and -my hosts included.
    public PersistedLogin? For(string? url) =>
        AuthMaterial.TenantOf(url) is { } tenant
            ? PersistedLogins.FirstOrDefault(l =>
                l.Enabled && string.Equals(AuthMaterial.TenantOf(l.Url), tenant, StringComparison.OrdinalIgnoreCase))
            : null;
}

/// <summary>Reads the auth material PnP would use, and turns it into the command to run next.</summary>
internal static class AuthMaterial
{
    private const string StoreDirectoryName = ".m365pnppowershell";

    private const string StoreFileName = "settings.json";

    private const string TokenCacheFileName = "pnp.msal.cache";

    private const string UrlPlaceholder = "https://<tenant>.sharepoint.com/sites/<site>";

    private static readonly string[] ClientIdVariables = ["ENTRAID_APP_ID", "ENTRAID_CLIENT_ID", "AZURE_CLIENT_ID"];

    private static readonly string[] CertificateVariables =
        ["ENTRAID_APP_CERTIFICATE_PATH", "ENTRAID_CLIENT_CERTIFICATE_PATH", "AZURE_CLIENT_CERTIFICATE_PATH"];

    /// <summary>Reads the store; <paramref name="storeDirectory"/> is for tests, production passes nothing.</summary>
    public static AuthFacts Gather(string? storeDirectory = null)
    {
        var directory = storeDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StoreDirectoryName);

        var (logins, error) = ReadStore(Path.Combine(directory, StoreFileName));
        var clientId = ClientIdVariables.FirstOrDefault(IsSet);

        return new AuthFacts(
            logins,
            File.Exists(Path.Combine(directory, TokenCacheFileName)),
            clientId,
            clientId is null ? null : Environment.GetEnvironmentVariable(clientId)?.Trim(),
            CertificateVariables.FirstOrDefault(IsSet) is { } path
                ? Environment.GetEnvironmentVariable(path)?.Trim()
                : null,
            error);
    }

    /// <summary>The tenant a SharePoint URL belongs to: contoso, for contoso / contoso-admin / contoso-my.</summary>
    public static string? TenantOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        // Bare hosts appear in the store and in what users type.
        var text = url.Contains("://", StringComparison.Ordinal) ? url.Trim() : "https://" + url.Trim();

        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed) || parsed.Host.Length == 0)
        {
            return null;
        }

        var label = parsed.Host.Split('.')[0].ToLowerInvariant();

        return label.EndsWith("-admin", StringComparison.Ordinal) ? label[..^6]
            : label.EndsWith("-my", StringComparison.Ordinal) ? label[..^3]
            : label;
    }

    /// <summary>Reports what is available and returns the one command to run.</summary>
    public static string Render(StringBuilder report, AuthFacts facts, string? targetUrl)
    {
        report.AppendLine("4. Auth material on this machine");

        if (facts.StoreError is not null)
        {
            report.AppendLine($"   NOTE - PnP's persisted-login store could not be read: {facts.StoreError}");
        }

        if (facts.PersistedLogins.Count == 0)
        {
            // An unreadable store is unknown, not empty.
            report.AppendLine(facts.StoreError is null
                ? "   No persisted logins, so nothing here can sign in without being told which app registration to use."
                : "   No persisted login could be read, so there may be one recorded that this report cannot see.");
        }
        else
        {
            report.AppendLine("   Persisted logins:");

            foreach (var login in facts.PersistedLogins)
            {
                report.AppendLine(
                    $"     {login.Url} -> app {login.ClientId ?? "(none recorded)"}{(login.Enabled ? string.Empty : " [disabled]")}");
            }

            report.AppendLine(facts.TokenCachePresent
                ? "   A cached token is present, so a connect to one of those tenants should not prompt at all."
                : "   NOTE - No token cache beside the store, so the first connect will still need a sign-in.");
        }

        // Named explicitly, or a model assumes one is set.
        report.AppendLine(facts.ClientIdVariable is null
            ? "   None of ENTRAID_APP_ID, ENTRAID_CLIENT_ID or AZURE_CLIENT_ID is set, so no client id comes from the environment."
            : $"   {facts.ClientIdVariable} is set to {facts.ClientId}, and PnP uses it when -ClientId is omitted.");

        if (facts.CertificatePath is not null)
        {
            report.AppendLine($"   A certificate is configured at {facts.CertificatePath}, so unattended auth is available.");
        }

        return Next(report, facts, targetUrl);
    }

    private static string Next(StringBuilder report, AuthFacts facts, string? targetUrl)
    {
        // One persisted tenant beats a placeholder.
        var url = string.IsNullOrWhiteSpace(targetUrl)
            ? facts.PersistedLogins.Count == 1 ? facts.PersistedLogins[0].Url : null
            : targetUrl.Trim();

        if (facts.For(url) is { } match)
        {
            // The store gives the app id; only the cache makes the connect silent.
            report.AppendLine(facts.TokenCachePresent
                ? $"   READY - {TenantOf(url)} is covered by a persisted login and a cached token."
                : $"   PARTIAL - {TenantOf(url)} has a persisted app id but no cached token, so this connect signs in once more.");

            return
                $"Run: Connect-PnPOnline -Url {url}   (no -ClientId needed; app {match.ClientId} is remembered for that tenant" +
                (facts.TokenCachePresent
                    ? ")"
                    : ". There is no cached token, so if this blocks it is waiting on a sign-in prompt you cannot see: have the user run it once in their own terminal with -PersistLogin.)");
        }

        if (facts.ClientIdVariable is not null)
        {
            return
                $"Run: Connect-PnPOnline -Url {url ?? UrlPlaceholder} -PersistLogin   " +
                $"(no -ClientId needed; {facts.ClientIdVariable} supplies it. -PersistLogin means later connects need no prompt.)";
        }

        if (facts.CertificatePath is { } certificate)
        {
            // -ClientId and -Tenant are mandatory here, and a .pfx usually has a password.
            return
                $"Run: Connect-PnPOnline -Url {url ?? UrlPlaceholder} -ClientId <app id> -Tenant {TenantOf(url) ?? "<tenant>"}.onmicrosoft.com " +
                $"-CertificatePath {certificate} -CertificatePassword (Read-Host -AsSecureString)   " +
                "(unattended, never prompts for a sign-in; omit -CertificatePassword only if the .pfx has none)";
        }

        return Bootstrap(report, url);
    }

    /// <summary>The cold start: no app registration anywhere, so one has to be created first.</summary>
    private static string Bootstrap(StringBuilder report, string? url)
    {
        // "No usable one here", not "none exists": a cleared login still lists its own app id.
        report.AppendLine("   BLOCKED - Nothing on this machine records a usable app registration for that tenant.");

        var site = url ?? UrlPlaceholder;

        // Only .sharepoint.com maps predictably onto .onmicrosoft.com.
        var tenant = url?.Contains(".sharepoint.com", StringComparison.OrdinalIgnoreCase) is true && TenantOf(url) is { } label
            ? label + ".onmicrosoft.com"
            : "<tenant>.onmicrosoft.com";

        return $"""
            Ask the user whether they already have an app registration for this tenant. If they do, no registration
            is needed: have them run the Connect-PnPOnline line below once, with that app id.

            If they do not, one has to be created, and that cannot be done from here -- it needs a browser and an
            administrator's consent. Give them these to run in their own PowerShell 7 terminal, then run this tool
            again.

            For a person signing in (the usual case):
              Register-PnPEntraIDAppForInteractiveLogin -ApplicationName "PnP PowerShell" -Tenant {tenant}
              Connect-PnPOnline -Url {site} -ClientId <app id> -PersistLogin

            For unattended use, registering an app with a certificate instead:
              Register-PnPEntraIDApp -ApplicationName "PnP PowerShell" -Tenant {tenant} -OutPath . -DeviceLogin
              Connect-PnPOnline -Url {site} -ClientId <app id> -Tenant {tenant} -CertificatePath <the .pfx it wrote> -CertificatePassword (Read-Host -AsSecureString)

            Ask which of the two the user wants before handing either over, and tell them what it grants: with no
            permissions named, each registers a default set that includes full control of every site, listed under
            pnp_get_best_practices section 'auth'.

            Either way the app registration needs admin consent first. -PersistLogin records the app id and caches
            the token, which is what lets this server connect afterwards with no prompt.
            """;
    }

    private static bool IsSet(string variable) =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable));

    private static (IReadOnlyList<PersistedLogin> Logins, string? Error) ReadStore(string path)
    {
        if (!File.Exists(path))
        {
            return ([], null);
        }

        try
        {
            var store = JsonSerializer.Deserialize(File.ReadAllText(path), AuthJsonContext.Default.PersistedLoginStore);

            return ([.. (store?.Cache ?? []).Where(l => !string.IsNullOrWhiteSpace(l.Url))], null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // An unreadable store is a diagnosis, not a crash.
            return ([], ex.Message);
        }
    }
}

// PnP writes this file in PascalCase.
[JsonSerializable(typeof(PersistedLoginStore))]
internal sealed partial class AuthJsonContext : JsonSerializerContext;
