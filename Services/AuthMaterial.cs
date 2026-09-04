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

/// <summary>One thing to run on the way to a connection, and who has to run it.</summary>
internal sealed record SetupStep(string Command, bool UserRuns, string Why)
{
    /// <summary>The one-line form, for when this is the only step left.</summary>
    public string Summary => UserRuns
        ? $"Ask the user to run this in their own PowerShell 7 terminal, then run 'pnp_diagnose_connection' again: {Command}   ({Why})"
        : $"Run: {Command}   ({Why})";
}

/// <summary>Reads the auth material PnP would use, and turns it into the commands to run next.</summary>
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

    /// <summary>Reports what is available and returns the commands to run, in order.</summary>
    public static IReadOnlyList<SetupStep> Render(StringBuilder report, AuthFacts facts, string? targetUrl)
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

    private static IReadOnlyList<SetupStep> Next(StringBuilder report, AuthFacts facts, string? targetUrl)
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

            return facts.TokenCachePresent
                ?
                [
                    new SetupStep(
                        $"Connect-PnPOnline -Url {url}",
                        UserRuns: false,
                        $"no -ClientId needed; app {match.ClientId} is remembered for that tenant, and the cached token means no prompt, so 'pnp_run_command' can run it"),
                ]
                :
                [
                    new SetupStep(
                        $"Connect-PnPOnline -Url {url} -PersistLogin",
                        UserRuns: true,
                        $"no -ClientId needed; app {match.ClientId} is remembered for that tenant. There is no cached token, so run from here " +
                        "this would hang waiting on a sign-in prompt you cannot see. -PersistLogin caches the token so later connects need none"),
                ];
        }

        if (facts.ClientIdVariable is not null)
        {
            return
            [
                new SetupStep(
                    $"Connect-PnPOnline -Url {url ?? UrlPlaceholder} -PersistLogin",
                    UserRuns: true,
                    $"no -ClientId needed; {facts.ClientIdVariable} supplies it. No persisted login covers that tenant, so this first sign-in opens " +
                    "a browser, which blocks and times out from inside this conversation. -PersistLogin means later connects need no prompt"),
            ];
        }

        if (facts.CertificatePath is { } certificate)
        {
            // -ClientId and -Tenant are mandatory here, and a .pfx usually has a password.
            return
            [
                new SetupStep(
                    $"Connect-PnPOnline -Url {url ?? UrlPlaceholder} -ClientId <app id> -Tenant {TenantOf(url) ?? "<tenant>"}.onmicrosoft.com " +
                    $"-CertificatePath {certificate} -CertificatePassword (Read-Host -AsSecureString)",
                    UserRuns: false,
                    "unattended, never prompts for a sign-in, so 'pnp_run_command' can run it once the user has supplied the app id; omit -CertificatePassword only if the .pfx has none"),
            ];
        }

        return Bootstrap(report, url);
    }

    /// <summary>The cold start: no app registration anywhere, so one has to be created first.</summary>
    private static IReadOnlyList<SetupStep> Bootstrap(StringBuilder report, string? url)
    {
        // "No usable one here", not "none exists": a cleared login still lists its own app id.
        report.AppendLine("   BLOCKED - Nothing on this machine records a usable app registration for that tenant.");

        var site = url ?? UrlPlaceholder;

        // Only .sharepoint.com maps predictably onto .onmicrosoft.com.
        var tenant = url?.Contains(".sharepoint.com", StringComparison.OrdinalIgnoreCase) is true && TenantOf(url) is { } label
            ? label + ".onmicrosoft.com"
            : "<tenant>.onmicrosoft.com";

        return
        [
            new SetupStep(
                $"$app = Register-PnPEntraIDAppForInteractiveLogin -ApplicationName \"PnP PowerShell\" -Tenant {tenant}",
                UserRuns: true,
                "it signs in through a browser and needs an administrator's consent, and neither can happen from inside this conversation. " +
                "Ask the user first whether the tenant already has an app registration for PnP PowerShell: if it does, skip this step and write " +
                "that app id in place of $app.'AzureAppId/ClientId' in the next one. With no permissions named it registers a default set that includes full control of every site, " +
                "listed under pnp_get_best_practices section 'auth'. For unattended use instead, register with a certificate: " +
                $"$app = Register-PnPEntraIDApp -ApplicationName \"PnP PowerShell\" -Tenant {tenant} -OutPath . -DeviceLogin, then connect with " +
                $"Connect-PnPOnline -Url {site} -ClientId $app.'AzureAppId/ClientId' -Tenant {tenant} -CertificatePath $app.'Pfx file'"),
            new SetupStep(
                $"Connect-PnPOnline -Url {site} -ClientId $app.'AzureAppId/ClientId' -PersistLogin",
                UserRuns: true,
                "the first sign-in opens a browser and PnP waits for it, so from here it would block and time out. -PersistLogin records the " +
                "app id and caches the token, which is what lets this server connect afterwards with no prompt"),
        ];
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
