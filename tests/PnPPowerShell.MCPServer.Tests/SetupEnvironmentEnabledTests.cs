using PnPPowerShell.MCPServer.Services;
using PnPPowerShell.MCPServer.Tools;
using Xunit.Abstractions;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>
/// The half of pnp_setup_environment that changes the machine. Every existing test covers the disabled
/// path, so the branch that actually runs Install-Module had no coverage at all — and it is the highest
/// consequence code in the server. Driven through playback, which answers from a fixture without
/// starting pwsh, so nothing here can install anything.
/// </summary>
public sealed class SetupEnvironmentEnabledTests(ITestOutputHelper output) : IAsyncDisposable
{
    private readonly PowerShellSessionManager _sessions = new();

    public async ValueTask DisposeAsync() => await _sessions.DisposeAsync();

    private static void WriteFixture(DirectoryInfo directory, string operation, string content)
    {
        var key = SessionTranscript.Key(string.Empty, operation);
        File.WriteAllText(Path.Combine(directory.FullName, key + ".transcript"), $"# key: {key}\n\n--- output ---\n{content}");
    }

    /// <summary>The gate is exact, and anything it does not recognise must fail closed.</summary>
    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("True", true)]
    [InlineData("1", false)]
    [InlineData("yes", false)]
    [InlineData("on", false)]
    [InlineData("false", false)]
    [InlineData(" true", false)]
    [InlineData("", false)]
    public async Task Only_an_explicit_true_permits_an_install(string value, bool allowed)
    {
        var directory = Directory.CreateTempSubdirectory("pnp-setup-gate");

        try
        {
            // A probe saying pwsh is present, so a permitted run reaches the install rather than stopping earlier.
            WriteFixture(directory, "environment-probe", """{"pwshVersion":"7.4.6","moduleVersion":null}""");
            WriteFixture(directory, "setup-environment", "Installed PnP.PowerShell 3.4.1. Next: call pnp_diagnose_connection with your site to see how to connect.");

            using var allow = new EnvVar("PNP_MCP_ALLOW_SETUP", value);
            using var replay = new EnvVar("PNP_MCP_REPLAY_DIR", directory.FullName);

            var report = ToolResults.Text(await PnPPowerShellTools.SetupEnvironment(_sessions, prerelease: false));

            output.WriteLine($"'{value}' -> {report.Split('\n')[0]}");

            Assert.Equal(allowed, !report.Contains("Environment setup is disabled", StringComparison.Ordinal));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>No pwsh means no install is even attempted, and the user is told what to do first.</summary>
    [Fact]
    public async Task Without_pwsh_it_refuses_before_running_anything()
    {
        var directory = Directory.CreateTempSubdirectory("pnp-setup-nopwsh");

        try
        {
            WriteFixture(directory, "environment-probe", """{"pwshVersion":null,"moduleVersion":null}""");

            // Deliberately no setup-environment fixture: reaching the install would fail playback, so a
            // passing test proves the install was never attempted.
            using var allow = new EnvVar("PNP_MCP_ALLOW_SETUP", "true");
            using var replay = new EnvVar("PNP_MCP_REPLAY_DIR", directory.FullName);

            var report = ToolResults.Text(await PnPPowerShellTools.SetupEnvironment(_sessions, prerelease: false));

            Assert.Contains("PowerShell 7", report, StringComparison.Ordinal);
            Assert.Contains("aka.ms/powershell", report, StringComparison.Ordinal);
            Assert.DoesNotContain("No recorded transcript", report, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_successful_install_reports_the_version_and_the_next_step()
    {
        var directory = Directory.CreateTempSubdirectory("pnp-setup-ok");

        try
        {
            WriteFixture(directory, "environment-probe", """{"pwshVersion":"7.4.6","moduleVersion":null}""");
            WriteFixture(directory, "setup-environment", "Installed PnP.PowerShell 3.4.1. Next: call pnp_diagnose_connection with your site to see how to connect.");

            using var allow = new EnvVar("PNP_MCP_ALLOW_SETUP", "true");
            using var replay = new EnvVar("PNP_MCP_REPLAY_DIR", directory.FullName);

            var report = ToolResults.Text(await PnPPowerShellTools.SetupEnvironment(_sessions, prerelease: false));

            Assert.Contains("Installed PnP.PowerShell 3.4.1", report, StringComparison.Ordinal);
            Assert.Contains("pnp_diagnose_connection", report, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>A failed install must surface the reason rather than read as success.</summary>
    [Fact]
    public async Task A_failed_install_surfaces_the_error()
    {
        var directory = Directory.CreateTempSubdirectory("pnp-setup-fail");

        try
        {
            WriteFixture(directory, "environment-probe", """{"pwshVersion":"7.4.6","moduleVersion":null}""");
            WriteFixture(directory, "setup-environment", "Error: No match was found for the specified search criteria and module name 'PnP.PowerShell'.");

            using var allow = new EnvVar("PNP_MCP_ALLOW_SETUP", "true");
            using var replay = new EnvVar("PNP_MCP_REPLAY_DIR", directory.FullName);

            var report = ToolResults.Text(await PnPPowerShellTools.SetupEnvironment(_sessions, prerelease: false));

            Assert.Contains("Error:", report, StringComparison.Ordinal);
            Assert.DoesNotContain("Installed PnP.PowerShell", report, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The destructive hint has to agree with what the install actually does. Clients auto-approve on
    /// this hint, and `-Force -AllowClobber` overwrites an existing install and can take over command
    /// names owned by other modules. Asserted against the command rather than restated as a constant,
    /// so dropping those flags and dropping the hint stay one decision instead of two.
    /// </summary>
    [Fact]
    public void The_destructive_hint_matches_what_the_install_command_does()
    {
        var command = PnPPowerShellTools.InstallModuleCommand(prerelease: false);
        var annotations = ToolCatalog.All.Single(t => t.ProtocolTool.Name == "pnp_setup_environment").ProtocolTool.Annotations;

        Assert.NotNull(annotations);
        Assert.False(annotations.ReadOnlyHint, "The setup tool changes the machine, so it is not read-only.");

        var overwrites = command.Contains("-Force", StringComparison.Ordinal) ||
                         command.Contains("-AllowClobber", StringComparison.Ordinal);

        Assert.Equal(overwrites, annotations.DestructiveHint);
    }

    /// <summary>The command offered when disabled must be the command that runs when enabled.</summary>
    // Two code paths describing one action is how a message and a behaviour drift apart.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_offered_command_and_the_executed_command_are_the_same_string(bool prerelease)
    {
        var command = PnPPowerShellTools.InstallModuleCommand(prerelease);

        Assert.Contains(command, PnPPowerShellTools.SetupDisabledMessage(prerelease), StringComparison.Ordinal);
        Assert.Equal(prerelease, command.Contains("-AllowPrerelease", StringComparison.Ordinal));

        // The install is scoped to the current user; a machine-wide install would need elevation and is
        // not something this server should ever attempt on someone's behalf.
        Assert.Contains("-Scope CurrentUser", command, StringComparison.Ordinal);
        Assert.DoesNotContain("-Scope AllUsers", command, StringComparison.Ordinal);
    }
}
