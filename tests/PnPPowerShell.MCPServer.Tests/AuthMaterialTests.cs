using PnPPowerShell.MCPServer.Services;
using System.Text;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>Each auth state must name a command with nothing left to guess. No tenant, no network, no pwsh.</summary>
public class AuthMaterialTests : IDisposable
{
    private const string App = "11111111-1111-4111-8111-111111111111";

    private readonly string _store = Path.Combine(Path.GetTempPath(), "pnp-auth-" + Guid.NewGuid().ToString("n"));
    private readonly List<IDisposable> _cleared = [];

    public AuthMaterialTests()
    {
        Directory.CreateDirectory(_store);

        // Cleared so a maintainer with these set gets CI's results.
        foreach (var name in (string[])["ENTRAID_APP_ID", "ENTRAID_CLIENT_ID", "AZURE_CLIENT_ID", "ENTRAID_APP_CERTIFICATE_PATH", "ENTRAID_CLIENT_CERTIFICATE_PATH", "AZURE_CLIENT_CERTIFICATE_PATH"])
        {
            _cleared.Add(new EnvVar(name, null));
        }
    }

    public void Dispose()
    {
        _cleared.ForEach(v => v.Dispose());
        Directory.Delete(_store, recursive: true);
        GC.SuppressFinalize(this);
    }

    private void Persist(string url) =>
        File.WriteAllText(
            Path.Combine(_store, "settings.json"),
            $$"""{"Cache":[{"Url":"{{url}}","ClientId":"{{App}}","Enabled":true}]}""");

    private string Advise(string? url)
    {
        var report = new StringBuilder();
        var next = AuthMaterial.Render(report, AuthMaterial.Gather(_store), url);

        return report + "\nNEXT: " + next;
    }

    [Theory]
    [InlineData("https://contoso.sharepoint.com/sites/marketing", "contoso")]
    [InlineData("https://contoso-admin.sharepoint.com", "contoso")]
    [InlineData("https://contoso-my.sharepoint.com/personal/x", "contoso")]
    [InlineData("contoso.sharepoint.com", "contoso")]
    [InlineData("not a url", null)]
    public void A_tenant_is_recognised_across_its_admin_and_personal_hosts(string url, string? expected) =>
        Assert.Equal(expected, AuthMaterial.TenantOf(url));

    [Fact]
    public void A_missing_or_unreadable_store_is_reported_rather_than_thrown()
    {
        Assert.Empty(AuthMaterial.Gather(_store).PersistedLogins);

        File.WriteAllText(Path.Combine(_store, "settings.json"), "{ not json");

        Assert.NotNull(AuthMaterial.Gather(_store).StoreError);
    }

    [Fact]
    public void A_persisted_tenant_is_told_to_connect_with_no_client_id()
    {
        Persist("https://contoso.sharepoint.com");
        File.WriteAllText(Path.Combine(_store, "pnp.msal.cache"), "cache");

        // Admin host too: the store resolves per tenant.
        foreach (var url in (string[])["https://contoso.sharepoint.com/sites/marketing", "https://contoso-admin.sharepoint.com"])
        {
            var report = Advise(url);

            Assert.Contains("READY", report);
            Assert.Contains($"NEXT: Run: Connect-PnPOnline -Url {url}", report);
            Assert.Contains("no -ClientId needed", report);
            Assert.DoesNotContain("<", report);
        }
    }

    [Fact]
    public void A_persisted_app_with_no_cached_token_does_not_promise_a_silent_connect()
    {
        // Only the token cache makes a connect silent.
        Persist("https://contoso.sharepoint.com");

        var report = Advise("https://contoso.sharepoint.com/sites/x");

        Assert.Contains("PARTIAL", report);
        Assert.DoesNotContain("should not prompt at all", report);
        Assert.Contains("waiting on a sign-in prompt you cannot see", report);
    }

    [Fact]
    public void With_no_client_id_anywhere_the_report_says_so_and_names_both_register_cmdlets()
    {
        var report = Advise("https://contoso.sharepoint.com/sites/hr");

        // Issue #19: the model assumed one of these was set.
        Assert.Contains("None of ENTRAID_APP_ID, ENTRAID_CLIENT_ID or AZURE_CLIENT_ID is set", report);

        Assert.Contains("BLOCKED", report);
        Assert.Contains("Register-PnPEntraIDAppForInteractiveLogin", report);
        Assert.Contains("Register-PnPEntraIDApp -ApplicationName", report);
        Assert.Contains("-Tenant contoso.onmicrosoft.com", report);
        Assert.Contains("full control of every site", report);

        // Register-PnPEntraIDAppForInteractiveLogin has no -Interactive switch.
        Assert.DoesNotContain("-Interactive", report);
        Assert.Contains("their own PowerShell 7 terminal", report);

        // A sovereign cloud does not map onto .onmicrosoft.com.
        Assert.Contains("-Tenant <tenant>.onmicrosoft.com", Advise("https://contoso.sharepoint.de/sites/hr"));
    }

    [Fact]
    public void A_client_id_in_the_environment_is_named_rather_than_left_to_be_guessed()
    {
        using var set = new EnvVar("ENTRAID_APP_ID", App);

        var report = Advise("https://contoso.sharepoint.com/sites/x");

        Assert.Contains($"ENTRAID_APP_ID is set to {App}", report);
        Assert.Contains("no -ClientId needed", report);
    }

    [Fact]
    public void AZURE_CLIENT_ID_alone_counts_as_a_client_id_from_the_environment()
    {
        using var set = new EnvVar("AZURE_CLIENT_ID", App);

        var facts = AuthMaterial.Gather(_store);

        Assert.Equal("AZURE_CLIENT_ID", facts.ClientIdVariable);
        Assert.Equal(App, facts.ClientId);
        Assert.Contains($"AZURE_CLIENT_ID is set to {App}", Advise("https://contoso.sharepoint.com/sites/x"));
    }

    [Fact]
    public void AZURE_CLIENT_CERTIFICATE_PATH_alone_counts_as_a_certificate()
    {
        using var set = new EnvVar("AZURE_CLIENT_CERTIFICATE_PATH", "/certs/pnp.pfx");

        Assert.Equal("/certs/pnp.pfx", AuthMaterial.Gather(_store).CertificatePath);
    }

    [Fact]
    public void A_certificate_gives_a_complete_unattended_command()
    {
        using var set = new EnvVar("ENTRAID_APP_CERTIFICATE_PATH", "/certs/pnp.pfx");

        var report = Advise("https://contoso.sharepoint.com/sites/x");

        Assert.Contains("-CertificatePath /certs/pnp.pfx", report);
        Assert.Contains("-Tenant contoso.onmicrosoft.com", report);
        Assert.Contains("-CertificatePassword", report);
    }

    [Fact]
    public void Section_four_appears_only_when_there_is_no_connection_to_use()
    {
        Persist("https://contoso.sharepoint.com");

        var connected = Render(new SessionFacts { Connected = true, Url = "https://contoso.sharepoint.com/sites/x", HelpUri = "https://x" });
        var not = Render(new SessionFacts { Connected = false });

        Assert.DoesNotContain("4. Auth material", connected);
        Assert.Contains("NEXT STEP: Ready.", connected);

        Assert.Contains("4. Auth material", not);
        Assert.Contains("NEXT STEP: Run: Connect-PnPOnline -Url https://contoso.sharepoint.com", not);
    }

    private string Render(SessionFacts session) =>
        ConnectionPreflight.Render(new PreflightFacts(
            "default",
            new EnvironmentFacts { PwshVersion = "7.5.4", ModuleVersion = "3.4.1", ModuleVersionCount = 1 },
            session,
            null,
            AuthMaterial.Gather(_store),
            TargetUrl: null));
}
