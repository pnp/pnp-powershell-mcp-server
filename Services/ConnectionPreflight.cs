using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using PnPPowerShell.MCPServer.Tools;

namespace PnPPowerShell.MCPServer.Services;

/// <summary>What the environment probe found; null means "could not be determined".</summary>
internal sealed class EnvironmentFacts
{
    public string? PwshVersion { get; set; }

    public string? PwshPath { get; set; }

    public string? ModuleVersion { get; set; }

    public int ModuleVersionCount { get; set; }

    public string? ProbeError { get; set; }

    /// <summary>True once the pwsh process actually started, whatever it went on to say.</summary>
    public bool PwshLaunched { get; set; }

    /// <summary>The probe never ran, so nothing here describes the real pwsh. Never recorded.</summary>
    [JsonIgnore]
    public bool ProbeUnavailable { get; set; }
}

/// <summary>What the session probe found about the connection this session holds.</summary>
internal sealed class SessionFacts
{
    public bool Connected { get; set; }

    public string? Url { get; set; }

    public string? TenantAdminUrl { get; set; }

    public string? ConnectionType { get; set; }

    public string? ConnectionMethod { get; set; }

    public string? Account { get; set; }

    public string? App { get; set; }

    public string? ClientId { get; set; }

    public string? Scopes { get; set; }

    public string? HelpUri { get; set; }
}

/// <summary>Everything <see cref="ConnectionPreflight.Render"/> needs, so the report can be tested without a tenant.</summary>
internal sealed record PreflightFacts(
    string SessionId,
    EnvironmentFacts Environment,
    SessionFacts? Session,
    string? SessionError,
    AuthFacts Auth,
    string? TargetUrl);

/// <summary>Answers the three questions that decide whether a command can run at all.</summary>
internal static class ConnectionPreflight
{
    private const string PwshMissingCause = "'pwsh' is not on PATH, so no PnP PowerShell command can run.";

    private const string ModuleMissingCause = "The PnP.PowerShell module is not installed for this user.";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(90);

    private const string ProbeTranscriptKey = "environment-probe";

    private const string EnvironmentProbeScript = """
        $ErrorActionPreference = 'SilentlyContinue'
        $mods = @(Get-Module -ListAvailable -Name PnP.PowerShell | Sort-Object Version -Descending)
        [PSCustomObject]@{
          pwshVersion = $PSVersionTable.PSVersion.ToString()
          pwshPath = (Get-Process -Id $PID).Path
          moduleVersion = if ($mods.Count -gt 0) { $mods[0].Version.ToString() } else { $null }
          moduleVersionCount = $mods.Count
        } | ConvertTo-Json -Compress
        """;

    private const string SessionProbeScript = """
        $__pnpDiag = [ordered]@{ connected = $false; url = $null; tenantAdminUrl = $null; connectionType = $null; connectionMethod = $null; account = $null; app = $null; clientId = $null; scopes = $null; helpUri = $null }
        try {
          $__pnpC = Get-PnPConnection
          $__pnpDiag.connected = $true
          $__pnpDiag.url = $__pnpC.Url
          $__pnpDiag.tenantAdminUrl = $__pnpC.TenantAdminUrl
          $__pnpDiag.connectionType = [string]$__pnpC.ConnectionType
          $__pnpDiag.connectionMethod = [string]$__pnpC.ConnectionMethod
          if ([string]$__pnpC.ConnectionMethod -notin @('ManagedIdentity','AzureADWorkloadIdentity')) { $__pnpDiag.clientId = $__pnpC.ClientId }
          if ($__pnpC.PSCredential) { $__pnpDiag.account = $__pnpC.PSCredential.UserName }
        } catch { }
        if ($__pnpDiag.connected) {
          try {
            $__pnpTok = Get-PnPAccessToken -Decoded -ErrorAction Stop
            if ($__pnpTok) {
              foreach ($__pnpClaim in @('upn','preferred_username','name')) {
                if (-not $__pnpDiag.account) { $__pnpDiag.account = $__pnpTok.Claims | Where-Object { $_.Type -eq $__pnpClaim } | Select-Object -First 1 -ExpandProperty Value }
              }
              $__pnpDiag.app = $__pnpTok.Claims | Where-Object { $_.Type -eq 'app_displayname' } | Select-Object -First 1 -ExpandProperty Value
              $__pnpDiag.scopes = ($__pnpTok.Claims | Where-Object { $_.Type -in @('scp','roles') } | Select-Object -ExpandProperty Value) -join ' '
            }
          } catch { }
        }
        try { $__pnpDiag.helpUri = ($ExecutionContext.InvokeCommand.GetCommand('Get-PnPWeb', [System.Management.Automation.CommandTypes]::All)).HelpUri } catch { }
        [PSCustomObject]$__pnpDiag | ConvertTo-Json -Depth 4 -Compress
        Remove-Variable -Name __pnpDiag, __pnpC, __pnpTok, __pnpClaim -ErrorAction SilentlyContinue
        """;

    public static async Task<PreflightFacts> GatherAsync(
        PowerShellSessionManager sessions,
        string? sessionId,
        string? targetUrl,
        CancellationToken cancellationToken)
    {
        var name = string.IsNullOrWhiteSpace(sessionId) ? PowerShellSessionManager.DefaultSessionId : sessionId.Trim();
        var auth = AuthMaterial.Gather();
        var environment = await ProbeEnvironmentAsync(cancellationToken);

        if (environment.PwshVersion is null || environment.ModuleVersion is null)
        {
            return new PreflightFacts(name, environment, null, null, auth, targetUrl);
        }

        var raw = await sessions.Get(sessionId).ExecuteAsync(SessionProbeScript, ProbeTimeout, cancellationToken, "preflight-probe");

        if (raw.StartsWith("Error:", StringComparison.Ordinal))
        {
            return new PreflightFacts(name, environment, null, raw, auth, targetUrl);
        }

        return new PreflightFacts(
            name, environment, Deserialize(raw, PreflightJsonContext.Default.SessionFacts), null, auth, targetUrl);
    }

    public static string Render(PreflightFacts facts)
    {
        var report = new StringBuilder();
        report.AppendLine($"PnP PowerShell preflight for session '{facts.SessionId}'.");
        report.AppendLine();

        var steps = new List<SetupStep>();
        var fatal = RenderPowerShell(report, facts.Environment, steps);
        var pwshMissing = steps.Count > 0;
        RenderModule(report, facts.Environment, steps, skipped: fatal is not null || pwshMissing);
        report.AppendLine();

        var next = fatal;

        if (fatal is null && steps.Count == 0)
        {
            report.AppendLine($"3. Connection (session '{facts.SessionId}')");
            next = RenderConnection(report, facts);
        }
        else
        {
            report.AppendLine("3. Connection");
            report.AppendLine("   SKIPPED - the two checks above have to pass first.");
        }

        // Auth material is read from files, so it is known even before pwsh is; only a fatal probe hides it.
        if (fatal is null && next is null)
        {
            report.AppendLine();
            steps.AddRange(AuthMaterial.Render(report, facts.Auth, facts.TargetUrl));
        }

        report.AppendLine();

        if (next is not null)
        {
            report.AppendLine($"NEXT STEP: {next}");
        }
        else if (steps.Count == 1)
        {
            report.AppendLine($"NEXT STEP: {steps[0].Summary}");
        }
        else
        {
            RenderPlan(report, facts, steps);
        }

        return report.ToString().TrimEnd();
    }

    /// <summary>The whole path from here to a connection, for when more than one step is missing.</summary>
    private static void RenderPlan(StringBuilder report, PreflightFacts facts, IReadOnlyList<SetupStep> steps)
    {
        report.AppendLine("NEXT STEP: More than one thing is missing, so here is the whole path from this machine to a connection, in order.");

        if (facts.TargetUrl is null && steps.Any(s => s.Command.Contains('<')))
        {
            report.AppendLine("   Run this tool again with targetUrl set to the site you want, and the <placeholders> below fill themselves in.");
        }

        report.AppendLine();
        report.AppendLine("   Already true:");

        foreach (var fact in AlreadyTrue(facts))
        {
            report.AppendLine($"     - {fact}");
        }

        report.AppendLine();
        report.AppendLine(steps.Any(s => s.UserRuns)
            ? "   Ask the user to run the steps marked USER in their own PowerShell 7 terminal, pasting each command as written. " +
              "This server can run the steps marked SERVER from here; each says which tool."
            : "   This server can run every step from here; each says which tool.");
        report.AppendLine();

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            report.AppendLine($"   {i + 1}. {(step.UserRuns ? "USER   " : "SERVER ")}{step.Command}");
            report.AppendLine($"      Why: {step.Why}.");
        }

        report.AppendLine();
        report.AppendLine("   Prove it worked:");

        if (steps.Any(s => s.UserRuns))
        {
            report.AppendLine("     - In that same terminal: Get-PnPWeb | Select-Object Title, Url   (prints the site once the sign-in has worked)");
        }

        report.AppendLine(
            $"     - Run 'pnp_diagnose_connection' again{(facts.TargetUrl is null ? string.Empty : " with the same targetUrl")}. " +
            "Every section above should read OK, and it should give one NEXT STEP this server can run, or Ready.");
        report.AppendLine("     - Then run Get-PnPWeb | Select-Object Title, Url through 'pnp_run_command'; it prints the site this session is connected to.");
    }

    private static IEnumerable<string> AlreadyTrue(PreflightFacts facts)
    {
        var environment = facts.Environment;
        var auth = facts.Auth;
        var any = false;

        if (environment.PwshVersion is not null)
        {
            any = true;
            yield return $"pwsh {environment.PwshVersion}{(environment.PwshPath is null ? string.Empty : $" at {environment.PwshPath}")}";
        }

        if (environment.ModuleVersion is not null)
        {
            any = true;
            yield return $"PnP.PowerShell {environment.ModuleVersion} is installed";
        }

        foreach (var login in auth.PersistedLogins.Where(l => l.Enabled))
        {
            any = true;
            yield return $"a persisted login for {login.Url} through app {login.ClientId ?? "(none recorded)"}";
        }

        if (auth.TokenCachePresent)
        {
            any = true;
            yield return "a cached token beside the persisted-login store";
        }

        if (auth.ClientIdVariable is not null)
        {
            any = true;
            yield return $"{auth.ClientIdVariable} supplies client id {auth.ClientId}";
        }

        if (auth.CertificatePath is not null)
        {
            any = true;
            yield return $"a certificate at {auth.CertificatePath}";
        }

        if (!any)
        {
            yield return "nothing yet; this machine starts from zero";
        }
    }

    private static string? RenderPowerShell(StringBuilder report, EnvironmentFacts environment, List<SetupStep> steps)
    {
        report.AppendLine("1. PowerShell 7 (pwsh)");

        if (environment.PwshVersion is not null)
        {
            report.AppendLine($"   OK - pwsh {environment.PwshVersion}{(environment.PwshPath is null ? string.Empty : $" at {environment.PwshPath}")}.");
            return null;
        }

        if (environment.ProbeUnavailable)
        {
            report.AppendLine($"   UNKNOWN - {environment.ProbeError}");
            return "Fix the playback fixture, or unset PNP_MCP_REPLAY_DIR to probe this machine for real. Nothing above describes the real pwsh install.";
        }

        if (!environment.PwshLaunched)
        {
            report.AppendLine($"   FAIL - {PwshMissingCause}");
            steps.Add(new SetupStep(
                "Install PowerShell 7.4 or later from https://aka.ms/powershell, then restart your MCP client so it picks up the new PATH",
                UserRuns: true,
                "this server never installs software, and it cannot see the new PATH until the client restarts it"));
            return null;
        }

        report.AppendLine(
            "   FAIL - pwsh started but did not report a usable version, so it is installed and broken rather than missing." +
            (environment.ProbeError is null ? string.Empty : $" It said: {environment.ProbeError}"));

        return "Run 'pwsh -NoProfile -Command $PSVersionTable.PSVersion' in a terminal and fix whatever it reports before using this server; a working install prints a version immediately.";
    }

    private static void RenderModule(StringBuilder report, EnvironmentFacts environment, List<SetupStep> steps, bool skipped)
    {
        report.AppendLine();
        report.AppendLine("2. PnP.PowerShell module");

        if (skipped)
        {
            report.AppendLine("   SKIPPED - pwsh has to be available before the module can be looked for.");

            // A missing pwsh already made a step; a broken probe made none, and gets no plan.
            if (steps.Count > 0)
            {
                steps.Add(InstallModuleStep(presumed: true));
            }

            return;
        }

        if (environment.ModuleVersion is null)
        {
            report.AppendLine($"   FAIL - {ModuleMissingCause}");
            steps.Add(InstallModuleStep(presumed: false));
            return;
        }

        report.AppendLine($"   OK - PnP.PowerShell {environment.ModuleVersion} is installed.");

        if (environment.ModuleVersionCount > 1)
        {
            report.AppendLine(
                $"   NOTE - {environment.ModuleVersionCount} versions are installed side by side; the session imports {environment.ModuleVersion}. " +
                "Remove the older ones with Uninstall-Module -Name PnP.PowerShell -AllVersions -Force, then reinstall, if behaviour differs from the docs.");
        }

        report.AppendLine(
            "   NOTE - This server never reaches the PowerShell Gallery, so it cannot tell you whether that is the newest release. " +
            "Check with: Find-Module -Name PnP.PowerShell | Select-Object Version");
    }

    private static SetupStep InstallModuleStep(bool presumed) =>
        new(
            PnPPowerShellTools.InstallModuleCommand(prerelease: false),
            UserRuns: !PnPPowerShellTools.SetupAllowed,
            (PnPPowerShellTools.SetupAllowed
                ? "PNP_MCP_ALLOW_SETUP is true, so 'pnp_setup_environment' runs this from here"
                : "this server installs software only when PNP_MCP_ALLOW_SETUP=true, and it is not set, so 'pnp_setup_environment' would only hand this command back") +
            (presumed ? ". The module could not be looked for without pwsh, so it is presumed missing" : string.Empty));

    /// <summary>The next step, or null when section 4 owns it because there is no connection to describe.</summary>
    private static string? RenderConnection(StringBuilder report, PreflightFacts facts)
    {
        if (facts.SessionError is not null)
        {
            var error = facts.SessionError.Trim();
            report.AppendLine($"   FAIL - The session did not answer: {error}");

            return error.Contains("busy running another command", StringComparison.OrdinalIgnoreCase)
                ? "Nothing is wrong: another command is still running in this session. Wait for it to finish, or use a different sessionId to work alongside it. Do not reset the session, which would terminate that command and drop its connection."
                : "Run 'pnp_reset_session' to discard the session, then try again.";
        }

        if (facts.Session is null)
        {
            report.AppendLine("   UNKNOWN - The session answered, but its reply could not be read.");
            return "Run 'pnp_reset_session' to start a clean session, then run 'pnp_diagnose_connection' again.";
        }

        var session = facts.Session;
        var next = DescribeConnection(report, session);

        if (string.IsNullOrWhiteSpace(session.HelpUri))
        {
            report.AppendLine(
                "   NOTE - This build of PnP.PowerShell reports no HelpUri for Get-PnPWeb, which means it predates the versions " +
                "that carry documentation links. 'pnp_get_command_docs' will fall back to a search instead of a direct link. " +
                "Run: Update-Module -Name PnP.PowerShell -Scope CurrentUser");
        }

        return next;
    }

    private static string? DescribeConnection(StringBuilder report, SessionFacts session)
    {
        if (!session.Connected)
        {
            report.AppendLine("   FAIL - This session holds no connection, so every PnP cmdlet will fail until one is made.");

            // Section 4 owns the next step here.
            return null;
        }

        report.AppendLine($"   OK - Connected as {session.Account ?? "an identity the connection does not expose"}.");
        report.AppendLine($"   URL: {session.Url ?? "(none)"}");
        report.AppendLine($"   Connection type: {session.ConnectionType ?? "(unknown)"}");
        report.AppendLine($"   Authenticated by: {session.ConnectionMethod ?? "(unknown)"}");

        if (!string.IsNullOrWhiteSpace(session.TenantAdminUrl))
        {
            report.AppendLine($"   Tenant admin URL: {session.TenantAdminUrl}");
        }

        if (!string.IsNullOrWhiteSpace(session.ClientId) || !string.IsNullOrWhiteSpace(session.App))
        {
            report.AppendLine($"   Signing in through app: {session.App ?? "(unnamed)"} ({session.ClientId ?? "client id not exposed"})");
        }

        if (!string.IsNullOrWhiteSpace(session.Scopes))
        {
            report.AppendLine($"   Graph token scopes: {session.Scopes}");
            report.AppendLine(
                "   NOTE - Those are the scopes on the Microsoft Graph token. PnP acquires a separate token per resource " +
                "on demand -- Graph, SharePoint, ARM -- so a cmdlet targeting a different resource is governed by that " +
                "resource's token, not this list.");
        }

        if (string.IsNullOrWhiteSpace(session.Url))
        {
            const string next =
                "This connection carries no site URL, so Get-PnPWeb, Get-PnPList and every other site-scoped cmdlet has " +
                "nothing to target. Reconnect with Connect-PnPOnline -Url https://<tenant>.sharepoint.com/sites/<site>.";
            report.AppendLine($"   WARN - {next}");
            return next;
        }

        if (!session.Url.Contains("-admin.sharepoint.com", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(session.ConnectionMethod, "DeviceLogin", StringComparison.OrdinalIgnoreCase))
            {
                const string next =
                    "Ready for site-scoped work only. Tenant-wide cmdlets such as Get-PnPTenantSite will refuse to run, " +
                    "because a device login is the one auth method PnP will not elevate to the admin site automatically. " +
                    "For those, reconnect with Connect-PnPOnline -Url https://<tenant>-admin.sharepoint.com -DeviceLogin.";

                report.AppendLine($"   WARN - {next}");
                return next;
            }

            report.AppendLine(
                "   NOTE - This is a site connection, not an admin one. Tenant-wide cmdlets such as Get-PnPTenantSite " +
                "still work from here, because PnP clones the context to https://<tenant>-admin.sharepoint.com on " +
                "demand, but they need the signed-in account to hold the SharePoint Administrator role. A 403 from " +
                "one of those is that role missing, not this URL being wrong.");
        }

        return "Ready. Run your command with 'pnp_run_command'.";
    }


    // Own pwsh process, so playback is handled here rather than at the session seam.
    internal static async Task<EnvironmentFacts> ProbeEnvironmentAsync(CancellationToken cancellationToken)
    {
        if (SessionTranscript.IsReplaying)
        {
            var replayed = SessionTranscript.Replay(EnvironmentProbeScript, ProbeTranscriptKey);

            // PwshLaunched is recorded, not assumed: assuming it rewrites "not on PATH" as "broken".
            return Deserialize(replayed, PreflightJsonContext.Default.EnvironmentFacts)
                ?? new EnvironmentFacts
                {
                    ProbeUnavailable = true,
                    ProbeError = $"playback is on (PNP_MCP_REPLAY_DIR) and the recorded environment probe could not be read: {replayed}",
                };
        }

        var facts = await LaunchProbeAsync(cancellationToken);
        SessionTranscript.Record(
            EnvironmentProbeScript,
            JsonSerializer.Serialize(facts, PreflightJsonContext.Default.EnvironmentFacts),
            ProbeTranscriptKey);

        return facts;
    }

    private static async Task<EnvironmentFacts> LaunchProbeAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(EnvironmentProbeScript);

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is Win32Exception or PlatformNotSupportedException)
        {
            return new EnvironmentFacts { ProbeError = ex.Message };
        }

        if (process is null)
        {
            return new EnvironmentFacts { ProbeError = "pwsh could not be started." };
        }

        using (process)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ProbeTimeout);

            var stdout = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);

                var text = await stdout;
                var error = await stderr;

                var facts = Deserialize(text, PreflightJsonContext.Default.EnvironmentFacts)
                    ?? new EnvironmentFacts { ProbeError = Summarise(error) };

                facts.PwshLaunched = true;
                return facts;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Kill(process);

                return new EnvironmentFacts
                {
                    PwshLaunched = true,
                    ProbeError = $"it did not answer within {ProbeTimeout.TotalSeconds:0} seconds.",
                };
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                throw;
            }
        }
    }

    private static string Summarise(string stderr)
    {
        var text = stderr.Trim();

        return text.Length switch
        {
            0 => "it exited without printing anything readable.",
            > 400 => text[..400] + "...",
            _ => text,
        };
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or SystemException)
        {
        }
    }

    private static T? Deserialize<T>(string raw, JsonTypeInfo<T> typeInfo) where T : class
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');

        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(raw[start..(end + 1)], typeInfo);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(EnvironmentFacts))]
[JsonSerializable(typeof(SessionFacts))]
internal sealed partial class PreflightJsonContext : JsonSerializerContext;
