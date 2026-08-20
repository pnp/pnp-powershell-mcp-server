using PnPPowerShell.MCPServer.Services;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>Every failure state must name a cause and the exact next command, with no tenant and no network.</summary>
public class ConnectionPreflightTests
{
    private static EnvironmentFacts Working() => new()
    {
        PwshVersion = "7.5.4",
        PwshPath = "/usr/bin/pwsh",
        ModuleVersion = "3.1.0",
        ModuleVersionCount = 1,
    };

    [Fact]
    public void Pwsh_missing_from_PATH_names_the_cause_and_the_install_step()
    {
        var report = ConnectionPreflight.Render(
            new PreflightFacts("default", new EnvironmentFacts { ProbeError = "The system cannot find the file specified." }, null, null));

        Assert.Contains("'pwsh' is not on PATH", report);
        Assert.Contains("https://aka.ms/powershell", report);
        Assert.Contains("NEXT STEP: Install PowerShell 7.4", report);
        Assert.Contains("2. PnP.PowerShell module", report);
        Assert.Contains("SKIPPED - pwsh has to be available", report);
    }

    [Fact]
    public void A_pwsh_that_starts_but_answers_nothing_is_not_reported_as_missing()
    {
        var report = ConnectionPreflight.Render(
            new PreflightFacts(
                "default",
                new EnvironmentFacts { PwshLaunched = true, ProbeError = "it did not answer within 90 seconds." },
                null,
                null));

        Assert.DoesNotContain("is not on PATH", report);
        Assert.Contains("installed and broken rather than missing", report);
        Assert.Contains("it did not answer within 90 seconds.", report);
    }

    [Fact]
    public void A_busy_session_is_not_told_to_reset_itself()
    {
        var report = ConnectionPreflight.Render(
            new PreflightFacts(
                "default",
                Working(),
                null,
                "Error: This session is busy running another command. Wait for it to finish, or end it with 'pnp_reset_session'."));

        Assert.Contains("another command is still running", report);
        Assert.DoesNotContain("NEXT STEP: Run 'pnp_reset_session'", report);
    }

    [Fact]
    public void A_missing_module_names_the_cause_and_the_install_command()
    {
        var environment = Working();
        environment.ModuleVersion = null;
        environment.ModuleVersionCount = 0;

        var report = ConnectionPreflight.Render(new PreflightFacts("default", environment, null, null));

        Assert.Contains("pwsh 7.5.4", report);
        Assert.Contains("PnP.PowerShell module is not installed", report);
        Assert.Contains("NEXT STEP: Run: Install-Module -Name PnP.PowerShell -Scope CurrentUser -Force", report);
    }

    [Fact]
    public void A_module_too_old_to_populate_HelpUri_says_so_and_gives_the_update_command()
    {
        var session = new SessionFacts
        {
            Connected = true,
            Url = "https://contoso.sharepoint.com/sites/team",
            ConnectionType = "O365",
            Account = "admin@contoso.onmicrosoft.com",
            App = "Contoso PnP App",
            HelpUri = null,
        };

        var report = ConnectionPreflight.Render(new PreflightFacts("default", Working(), session, null));

        Assert.Contains("no HelpUri for Get-PnPWeb", report);
        Assert.Contains("Update-Module -Name PnP.PowerShell -Scope CurrentUser", report);

        Assert.Contains("Connected as admin@contoso.onmicrosoft.com", report);
        Assert.Contains("Signing in through app: Contoso PnP App", report);
    }

    [Fact]
    public async Task Stripping_pwsh_off_PATH_really_does_produce_the_missing_branch()
    {
        var empty = Directory.CreateTempSubdirectory("pnp-preflight-");

        try
        {
            using var _ = new EnvVar("PATH", empty.FullName);
            await using var sessions = new PowerShellSessionManager();

            var facts = await ConnectionPreflight.GatherAsync(sessions, null, CancellationToken.None);

            Assert.False(facts.Environment.PwshLaunched);
            Assert.Null(facts.Environment.PwshVersion);
            Assert.Null(facts.Session);
            Assert.Contains("'pwsh' is not on PATH", ConnectionPreflight.Render(facts));
        }
        finally
        {
            empty.Delete(recursive: true);
        }
    }

    [RequiresPnPFact]
    public async Task The_probe_reads_the_real_environment_and_session()
    {
        await using var sessions = new PowerShellSessionManager();

        var facts = await ConnectionPreflight.GatherAsync(sessions, null, CancellationToken.None);

        Assert.True(facts.Environment.PwshLaunched);
        Assert.Matches(@"^\d+\.\d+", facts.Environment.PwshVersion ?? string.Empty);
        Assert.NotNull(facts.Environment.PwshPath);
        Assert.Matches(@"^\d+\.\d+", facts.Environment.ModuleVersion ?? string.Empty);
        Assert.True(facts.Environment.ModuleVersionCount >= 1);

        Assert.Null(facts.SessionError);
        Assert.NotNull(facts.Session);
        Assert.False(facts.Session!.Connected);
    }

    [Fact]
    public void A_connection_with_no_site_url_is_called_out_as_having_nothing_to_target()
    {
        var session = new SessionFacts
        {
            Connected = true,
            Url = null,
            ConnectionType = "O365",
            Account = "mi@contoso.onmicrosoft.com",
            HelpUri = "https://pnp.github.io/powershell/cmdlets/Get-PnPWeb.html",
        };

        var report = ConnectionPreflight.Render(new PreflightFacts("default", Working(), session, null));

        Assert.Contains("carries no site URL", report);
        Assert.DoesNotContain("Graph-only", report);
        Assert.DoesNotContain("Ready. Run your command", report);
    }

    [Fact]
    public void A_device_login_is_not_reported_as_ready_for_tenant_wide_work()
    {
        var session = new SessionFacts
        {
            Connected = true,
            Url = "https://contoso.sharepoint.com/sites/team",
            ConnectionMethod = "DeviceLogin",
            Account = "admin@contoso.onmicrosoft.com",
            HelpUri = "https://pnp.github.io/powershell/cmdlets/Get-PnPWeb.html",
        };

        var report = ConnectionPreflight.Render(new PreflightFacts("default", Working(), session, null));

        Assert.Contains("will refuse to run", report);
        Assert.Contains("NEXT STEP: Ready for site-scoped work only", report);
        Assert.DoesNotContain("NEXT STEP: Ready. Run your command", report);
    }

    [Fact]
    public void Reported_scopes_are_labelled_as_the_Graph_tokens_own()
    {
        var session = new SessionFacts
        {
            Connected = true,
            Url = "https://contoso.sharepoint.com/sites/team",
            Account = "admin@contoso.onmicrosoft.com",
            Scopes = "Group.ReadWrite.All User.Read",
            HelpUri = "https://pnp.github.io/powershell/cmdlets/Get-PnPWeb.html",
        };

        var report = ConnectionPreflight.Render(new PreflightFacts("default", Working(), session, null));

        Assert.Contains("Graph token scopes: Group.ReadWrite.All User.Read", report);
        Assert.Contains("a separate token per resource", report);
    }

    [Fact]
    public void A_session_with_no_connection_says_what_to_run_to_get_one()
    {
        var report = ConnectionPreflight.Render(
            new PreflightFacts("default", Working(), new SessionFacts { HelpUri = "https://pnp.github.io/powershell/cmdlets/Get-PnPWeb.html" }, null));

        Assert.Contains("holds no connection", report);
        Assert.Contains("Connect-PnPOnline -Url", report);
        Assert.Contains("Register-PnPEntraIDAppForInteractiveLogin", report);
    }
}
