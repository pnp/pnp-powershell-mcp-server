using PnPPowerShell.MCPServer.Services;
using PnPPowerShell.MCPServer.Tools;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>The setup tool must not change the machine unless the operator opts in. No pwsh, no network.</summary>
public sealed class SetupEnvironmentTests : IAsyncDisposable
{
    private readonly PowerShellSessionManager _sessions = new();
    private readonly EnvVar _allow = new("PNP_MCP_ALLOW_SETUP", null);

    public async ValueTask DisposeAsync()
    {
        _allow.Dispose();
        await _sessions.DisposeAsync();
    }

    [Theory]
    [InlineData(false, "-Force -AllowClobber")]
    [InlineData(true, "-AllowPrerelease")]
    public void The_install_command_names_the_module_and_toggles_prerelease(bool prerelease, string expected)
    {
        var command = PnPPowerShellTools.InstallModuleCommand(prerelease);

        Assert.StartsWith("Install-Module -Name PnP.PowerShell -Scope CurrentUser", command);
        Assert.Contains(expected, command);
        Assert.Equal(prerelease, command.Contains("-AllowPrerelease", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_setup_disabled_nothing_is_installed_and_the_manual_command_is_given()
    {
        // PNP_MCP_ALLOW_SETUP is cleared by the fixture, so this is the default experience.
        var report = await PnPPowerShellTools.SetupEnvironment(_sessions, prerelease: false);

        Assert.Contains("Environment setup is disabled", report);
        Assert.Contains("PNP_MCP_ALLOW_SETUP=true", report);
        Assert.Contains(PnPPowerShellTools.InstallModuleCommand(false), report);
    }

    [Fact]
    public async Task The_disabled_message_offers_the_prerelease_command_when_asked()
    {
        var report = await PnPPowerShellTools.SetupEnvironment(_sessions, prerelease: true);

        Assert.Contains("-AllowPrerelease", report);
    }

    [Fact]
    public void The_disabled_message_never_touches_the_tenant()
    {
        var report = PnPPowerShellTools.SetupDisabledMessage(prerelease: false);

        Assert.DoesNotContain("Connect-PnPOnline", report);
        Assert.DoesNotContain("Register-PnPEntraID", report);
        Assert.Contains("does not sign you in", report);
    }
}
