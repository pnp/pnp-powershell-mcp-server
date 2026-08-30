using PnPPowerShell.MCPServer.Services;
using PnPPowerShell.MCPServer.Tools;
using System.Text.Json;
using Xunit.Abstractions;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>
/// The readiness half of pnp_ping on machines this developer machine is not: no pwsh, or pwsh without
/// the module. Manufactured through the recorded probe, because those are the states the tool exists
/// to report and the ones CI's bare runner actually hits.
/// </summary>
public class PingReadinessTests(ITestOutputHelper output)
{
    private static async Task<JsonElement> PingWithProbe(string probeJson, DirectoryInfo directory)
    {
        // The probe's transcript key, written the way ConnectionPreflight replays it.
        var key = SessionTranscript.Key(string.Empty, "environment-probe");
        File.WriteAllText(Path.Combine(directory.FullName, key + ".transcript"), $"# key: {key}\n\n--- output ---\n{probeJson}");

        using var replay = new EnvVar("PNP_MCP_REPLAY_DIR", directory.FullName);
        await using var sessions = new PowerShellSessionManager();

        return (await PnPPowerShellTools.Ping(sessions, includeReadiness: true)).StructuredContent!.Value;
    }

    [Theory]
    // pwsh present with the module, pwsh present without it, and no pwsh at all.
    [InlineData("""{"pwshVersion":"7.4.6","moduleVersion":"3.4.1"}""", true, true, true)]
    [InlineData("""{"pwshVersion":"7.4.6","moduleVersion":null}""", true, false, false)]
    [InlineData("""{"pwshVersion":null,"moduleVersion":null}""", false, false, false)]
    public async Task Readiness_reports_each_machine_state(string probe, bool pwsh, bool module, bool hasVersion)
    {
        var directory = Directory.CreateTempSubdirectory("pnp-readiness");

        try
        {
            var health = await PingWithProbe(probe, directory);

            output.WriteLine($"{probe} -> {health.GetRawText()}");

            Assert.Equal(pwsh, health.GetProperty("pwshAvailable").GetBoolean());
            Assert.Equal(module, health.GetProperty("pnpModuleInstalled").GetBoolean());

            // The invariant that replaced #22's "the key is always present": it tracks whether there is
            // a version. This is the case the original assertion would have failed on a bare runner.
            Assert.Equal(hasVersion, health.TryGetProperty("pnpModuleVersion", out _));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Each state must be said plainly in prose too, and must name the way out.</summary>
    [Theory]
    [InlineData("""{"pwshVersion":"7.4.6","moduleVersion":"3.4.1"}""", "3.4.1")]
    [InlineData("""{"pwshVersion":"7.4.6","moduleVersion":null}""", "pnp_setup_environment")]
    [InlineData("""{"pwshVersion":null,"moduleVersion":null}""", "aka.ms/powershell")]
    public async Task Readiness_prose_names_the_next_step(string probe, string expected)
    {
        var directory = Directory.CreateTempSubdirectory("pnp-readiness-text");

        try
        {
            var key = SessionTranscript.Key(string.Empty, "environment-probe");
            File.WriteAllText(Path.Combine(directory.FullName, key + ".transcript"), $"# key: {key}\n\n--- output ---\n{probe}");

            using var replay = new EnvVar("PNP_MCP_REPLAY_DIR", directory.FullName);
            await using var sessions = new PowerShellSessionManager();

            var text = ToolResults.Text(await PnPPowerShellTools.Ping(sessions, includeReadiness: true));

            output.WriteLine(text.Split('\n').First(l => l.StartsWith("Readiness:", StringComparison.Ordinal)));

            Assert.Contains(expected, text, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Skipping the probe must not launch pwsh, which is the point of the flag.</summary>
    [Fact]
    public async Task Skipping_readiness_touches_no_process()
    {
        var directory = Directory.CreateTempSubdirectory("pnp-readiness-none");

        try
        {
            // No probe fixture at all: if the tool probed, replay would fail rather than answer.
            using var replay = new EnvVar("PNP_MCP_REPLAY_DIR", directory.FullName);
            await using var sessions = new PowerShellSessionManager();

            var result = await PnPPowerShellTools.Ping(sessions, includeReadiness: false);
            var health = result.StructuredContent!.Value;

            Assert.Equal("ok", health.GetProperty("status").GetString());
            Assert.False(health.TryGetProperty("pwshAvailable", out _));
            Assert.Contains("not checked", ToolResults.Text(result), StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
