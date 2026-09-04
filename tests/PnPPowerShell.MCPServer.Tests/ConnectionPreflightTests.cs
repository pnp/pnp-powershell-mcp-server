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
            new PreflightFacts("default", new EnvironmentFacts { ProbeError = "The system cannot find the file specified." }, null, null, AuthFacts.None, null));

        Assert.Contains("'pwsh' is not on PATH", report);
        Assert.Contains("https://aka.ms/powershell", report);
        Assert.Contains("1. USER   Install PowerShell 7.4", report);
        Assert.Contains("2. PnP.PowerShell module", report);
        Assert.Contains("SKIPPED - pwsh has to be available", report);
    }

    private static async Task<string> Replay(string environment, string? session, AuthFacts auth, string? targetUrl)
    {
        var directory = Directory.CreateTempSubdirectory("pnp-coldstart-");

        try
        {
            Fixture(directory, "environment-probe", environment);

            if (session is not null)
            {
                Fixture(directory, "preflight-probe", session);
            }

            using var replay = new EnvVar("PNP_MCP_REPLAY_DIR", directory.FullName);
            await using var sessions = new PowerShellSessionManager();

            var facts = await ConnectionPreflight.GatherAsync(sessions, null, targetUrl, CancellationToken.None);

            // The environment and session come from the fixtures; the store is this machine's, so it is pinned here.
            return ConnectionPreflight.Render(facts with { Auth = auth });
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static void Fixture(DirectoryInfo directory, string operation, string output)
    {
        var key = SessionTranscript.Key(string.Empty, operation);
        File.WriteAllText(Path.Combine(directory.FullName, $"{operation}-{key}.transcript"), $"# key: {key}\n\n--- output ---\n{output}");
    }

    private const string Site = "https://contoso.sharepoint.com/sites/marketing";

    private const string NothingInstalled = """{"pwshVersion":null,"pwshPath":null,"moduleVersion":null,"moduleVersionCount":0,"probeError":"The system cannot find the file specified.","pwshLaunched":false}""";

    private const string ModuleInstalled = """{"pwshVersion":"7.5.4","pwshPath":"C:\\Program Files\\PowerShell\\7\\pwsh.exe","moduleVersion":"3.4.1","moduleVersionCount":1,"probeError":null,"pwshLaunched":true}""";

    private const string NotConnected = """{"connected":false,"helpUri":"https://pnp.github.io/powershell/cmdlets/Get-PnPWeb.html"}""";

    private const string Connected = """{"connected":true,"url":"https://contoso.sharepoint.com/sites/marketing","connectionType":"O365","connectionMethod":"Credentials","account":"admin@contoso.onmicrosoft.com","helpUri":"https://pnp.github.io/powershell/cmdlets/Get-PnPWeb.html"}""";

    private static string Plan(string report)
    {
        var index = report.IndexOf("NEXT STEP:", StringComparison.Ordinal);
        Assert.True(index >= 0, "The preflight report has no NEXT STEP line.");

        return report[index..];
    }

    [PlaybackFact]
    public async Task A_machine_with_nothing_installed_gets_the_whole_path_with_no_placeholders()
    {
        var report = await Replay(NothingInstalled, null, AuthFacts.None, Site);
        var plan = Plan(report);

        Assert.Contains("Already true:", plan);
        Assert.Contains("nothing yet", plan);
        Assert.Contains("1. USER   Install PowerShell 7.4", plan);
        Assert.Contains("2. USER   Install-Module -Name PnP.PowerShell -Scope CurrentUser -Force", plan);
        Assert.Contains("3. USER   $app = Register-PnPEntraIDAppForInteractiveLogin -ApplicationName \"PnP PowerShell\" -Tenant contoso.onmicrosoft.com", plan);
        Assert.Contains($"4. USER   Connect-PnPOnline -Url {Site} -ClientId $app.'AzureAppId/ClientId' -PersistLogin", plan);
        Assert.Contains("Prove it worked:", plan);
        Assert.Contains("Get-PnPWeb", plan);

        // Who runs it, and why.
        Assert.Contains("Ask the user", plan);
        Assert.Contains("administrator's consent", plan);
        Assert.Contains("block and time out", plan);
        Assert.Contains("PNP_MCP_ALLOW_SETUP", plan);

        Assert.DoesNotContain("<", plan);
    }

    [PlaybackFact]
    public async Task A_module_with_no_connection_and_no_app_gets_the_path_from_registration_onwards()
    {
        var report = await Replay(ModuleInstalled, NotConnected, AuthFacts.None, Site);
        var plan = Plan(report);

        Assert.Contains("- pwsh 7.5.4 at C:\\Program Files\\PowerShell\\7\\pwsh.exe", plan);
        Assert.Contains("- PnP.PowerShell 3.4.1 is installed", plan);
        Assert.DoesNotContain("Install-Module", plan);
        Assert.DoesNotContain("Install PowerShell", plan);
        Assert.Contains("1. USER   $app = Register-PnPEntraIDAppForInteractiveLogin", plan);
        Assert.Contains("2. USER   Connect-PnPOnline -Url", plan);
        Assert.DoesNotContain("<", plan);
    }

    [PlaybackFact]
    public async Task A_persisted_login_with_a_cached_token_leaves_one_step_and_no_plan()
    {
        var login = new PersistedLogin { Url = "https://contoso.sharepoint.com", ClientId = "11111111-1111-4111-8111-111111111111", Enabled = true };
        var report = await Replay(ModuleInstalled, NotConnected, new AuthFacts([login], true, null, null, null, null), Site);

        Assert.Contains("READY - contoso is covered", report);
        Assert.Contains($"NEXT STEP: Run: Connect-PnPOnline -Url {Site}   (no -ClientId needed", report);
        Assert.DoesNotContain("Already true:", report);
        Assert.DoesNotContain("<", Plan(report));
    }

    [Fact]
    public void With_setup_allowed_and_a_cached_login_every_step_is_the_servers()
    {
        using var allowed = new EnvVar("PNP_MCP_ALLOW_SETUP", "true");

        var environment = Working();
        environment.ModuleVersion = null;
        environment.ModuleVersionCount = 0;
        var login = new PersistedLogin { Url = "https://contoso.sharepoint.com", ClientId = "11111111-1111-4111-8111-111111111111", Enabled = true };

        var report = ConnectionPreflight.Render(
            new PreflightFacts("default", environment, null, null, new AuthFacts([login], true, null, null, null, null), Site));
        var plan = Plan(report);

        Assert.Contains("1. SERVER Install-Module -Name PnP.PowerShell", plan);
        Assert.Contains("'pnp_setup_environment' runs this from here", plan);
        Assert.Contains($"2. SERVER Connect-PnPOnline -Url {Site}", plan);
        Assert.Contains("This server can run every step from here", plan);
        Assert.DoesNotContain("USER", plan);
        Assert.DoesNotContain("In that same terminal", plan);
    }

    [PlaybackFact]
    public async Task A_connected_session_keeps_the_report_as_it_was()
    {
        var report = await Replay(ModuleInstalled, Connected, AuthFacts.None, Site);

        Assert.Contains("Connected as admin@contoso.onmicrosoft.com", report);
        Assert.Contains("NEXT STEP: Ready. Run your command with 'pnp_run_command'.", report);
        Assert.DoesNotContain("Already true:", report);
        Assert.DoesNotContain("4. Auth material", report);
        Assert.DoesNotContain("Register-PnPEntraIDApp", report);
    }

    [Fact]
    public void A_pwsh_that_starts_but_answers_nothing_is_not_reported_as_missing()
    {
        var report = ConnectionPreflight.Render(
            new PreflightFacts(
                "default",
                new EnvironmentFacts { PwshLaunched = true, ProbeError = "it did not answer within 90 seconds." },
                null,
                null,
                AuthFacts.None,
                null));

        Assert.DoesNotContain("is not on PATH", report);
        Assert.Contains("installed and broken rather than missing", report);
        Assert.Contains("it did not answer within 90 seconds.", report);
    }

    [Fact]
    public void An_unreadable_playback_fixture_is_not_blamed_on_the_pwsh_install()
    {
        var report = ConnectionPreflight.Render(
            new PreflightFacts(
                "default",
                new EnvironmentFacts { ProbeUnavailable = true, ProbeError = "playback is on and the probe could not be read." },
                null,
                null,
                AuthFacts.None,
                null));

        Assert.DoesNotContain("is not on PATH", report);
        Assert.DoesNotContain("installed and broken", report);
        Assert.Contains("PNP_MCP_REPLAY_DIR", report);
    }

    [Fact]
    public void A_busy_session_is_not_told_to_reset_itself()
    {
        var report = ConnectionPreflight.Render(
            new PreflightFacts(
                "default",
                Working(),
                null,
                "Error: This session is busy running another command. Wait for it to finish, or end it with 'pnp_reset_session'.",
                AuthFacts.None,
                null));

        Assert.Contains("another command is still running", report);
        Assert.DoesNotContain("NEXT STEP: Run 'pnp_reset_session'", report);
    }

    [Fact]
    public void A_missing_module_names_the_cause_and_the_install_command()
    {
        var environment = Working();
        environment.ModuleVersion = null;
        environment.ModuleVersionCount = 0;

        var report = ConnectionPreflight.Render(new PreflightFacts("default", environment, null, null, AuthFacts.None, null));

        Assert.Contains("pwsh 7.5.4", report);
        Assert.Contains("PnP.PowerShell module is not installed", report);
        Assert.Contains("Install-Module -Name PnP.PowerShell -Scope CurrentUser -Force", report);

        // Says who runs it: as a bare instruction the model runs it here and is refused.
        Assert.Contains("Ask the user", report);
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

        var report = ConnectionPreflight.Render(new PreflightFacts("default", Working(), session, null, AuthFacts.None, null));

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

            var facts = await ConnectionPreflight.GatherAsync(sessions, null, null, CancellationToken.None);

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

    [BarePnPFact]
    public async Task A_machine_without_the_prerequisites_is_told_exactly_what_to_install()
    {
        await using var sessions = new PowerShellSessionManager();

        var facts = await ConnectionPreflight.GatherAsync(sessions, null, null, CancellationToken.None);
        var report = ConnectionPreflight.Render(facts);

        Assert.Null(facts.Session);

        if (facts.Environment.PwshLaunched)
        {
            Assert.Null(facts.Environment.ModuleVersion);
            Assert.Contains("PnP.PowerShell module is not installed", report);
            Assert.Contains("1. USER   Install-Module -Name PnP.PowerShell -Scope CurrentUser -Force", report);
        }
        else
        {
            Assert.Contains("'pwsh' is not on PATH", report);
            Assert.Contains("1. USER   Install PowerShell 7.4", report);
        }
    }

    [RequiresPnPFact]
    public async Task The_probe_reads_the_real_environment_and_session()
    {
        await using var sessions = new PowerShellSessionManager();

        var facts = await ConnectionPreflight.GatherAsync(sessions, null, null, CancellationToken.None);

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

        var report = ConnectionPreflight.Render(new PreflightFacts("default", Working(), session, null, AuthFacts.None, null));

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

        var report = ConnectionPreflight.Render(new PreflightFacts("default", Working(), session, null, AuthFacts.None, null));

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

        var report = ConnectionPreflight.Render(new PreflightFacts("default", Working(), session, null, AuthFacts.None, null));

        Assert.Contains("Graph token scopes: Group.ReadWrite.All User.Read", report);
        Assert.Contains("a separate token per resource", report);
    }

    [Fact]
    public void A_session_with_no_connection_says_what_to_run_to_get_one()
    {
        var report = ConnectionPreflight.Render(
            new PreflightFacts("default", Working(), new SessionFacts { HelpUri = "https://pnp.github.io/powershell/cmdlets/Get-PnPWeb.html" }, null, AuthFacts.None, null));

        Assert.Contains("holds no connection", report);
        Assert.Contains("Connect-PnPOnline -Url", report);
        Assert.Contains("Register-PnPEntraIDAppForInteractiveLogin", report);
    }
}
