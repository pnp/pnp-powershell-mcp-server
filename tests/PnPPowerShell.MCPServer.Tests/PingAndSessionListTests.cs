using PnPPowerShell.MCPServer.Services;
using PnPPowerShell.MCPServer.Tools;
using System.Text.Json;

namespace PnPPowerShell.MCPServer.Tests;

public sealed class PingAndSessionListTests : IAsyncDisposable
{
    private readonly PowerShellSessionManager _sessions = new();

    public async ValueTask DisposeAsync() => await _sessions.DisposeAsync();

    // Both tools used to hand-build their payload into a string; the assertions now read the structured
    // half, which is the same data the client reads rather than a rendering of it. The readiness cases
    // came from #22 and are kept as they were, only re-pointed at that half.
    private async Task<JsonElement> PingAsync(bool includeReadiness = false) =>
        (await PnPPowerShellTools.Ping(_sessions, includeReadiness)).StructuredContent!.Value;

    [Fact]
    public async Task Ping_reports_the_expected_fields()
    {
        var health = await PingAsync();

        Assert.Equal("ok", health.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(health.GetProperty("version").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(health.GetProperty("packageVersion").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(health.GetProperty("uptime").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(health.GetProperty("startedUtc").GetString()));
        Assert.True(health.TryGetProperty("readOnlyMode", out _));
        Assert.True(health.TryGetProperty("activeSessions", out _));
    }

    [Fact]
    public async Task Ping_reports_zero_sessions_on_a_fresh_manager() =>
        Assert.Equal(0, (await PingAsync()).GetProperty("activeSessions").GetInt32());

    [Fact]
    public async Task Ping_reports_active_sessions_after_one_is_created()
    {
        _sessions.Get("alpha");

        Assert.Equal(1, (await PingAsync()).GetProperty("activeSessions").GetInt32());
    }

    [Fact]
    public async Task Ping_reflects_readonly_mode()
    {
        using var _ = new EnvVar("PNP_MCP_READONLY", "true");

        Assert.True((await PingAsync()).GetProperty("readOnlyMode").GetBoolean());
    }

    /// <summary>Not probing must be distinguishable from probing and finding nothing.</summary>
    // The fields are omitted rather than emitted as false: a client reading false would send the user to
    // install something that may already be present.
    [Fact]
    public async Task Ping_without_readiness_omits_the_environment_fields()
    {
        var health = await PingAsync(includeReadiness: false);

        Assert.False(health.TryGetProperty("pwshAvailable", out _));
        Assert.False(health.TryGetProperty("pnpModuleInstalled", out _));
        Assert.Contains("not checked", ToolResults.Text(await PnPPowerShellTools.Ping(_sessions, false)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ping_with_readiness_reports_pwsh_and_module_state_as_booleans()
    {
        var health = await PingAsync(includeReadiness: true);

        // Values depend on the machine; the contract is that the keys exist and are typed.
        Assert.True(health.TryGetProperty("pwshAvailable", out var pwsh));
        Assert.True(pwsh.ValueKind is JsonValueKind.True or JsonValueKind.False);
        Assert.True(health.TryGetProperty("pnpModuleInstalled", out var installed));
        Assert.True(installed.ValueKind is JsonValueKind.True or JsonValueKind.False);
    }

    /// <summary>
    /// The version key tracks whether there is a version, rather than always appearing as null.
    ///
    /// A deliberate change from #22, which emitted "pnpModuleVersion": null when the module was absent
    /// and asserted the key always existed. Every optional field in this payload is omitted when null,
    /// so an explicit null here would be the one exception — and `pnpModuleInstalled: false` already
    /// says there is no version. Asserted as a conditional so it holds on a machine with the module and
    /// on the bare CI runner without it, which is where the original assertion would have failed.
    /// </summary>
    [Fact]
    public async Task Ping_reports_a_module_version_exactly_when_a_module_is_installed()
    {
        var health = await PingAsync(includeReadiness: true);

        var installed = health.GetProperty("pnpModuleInstalled").GetBoolean();
        var hasVersion = health.TryGetProperty("pnpModuleVersion", out var version);

        Assert.Equal(installed, hasVersion);

        if (installed)
        {
            Assert.False(string.IsNullOrWhiteSpace(version.GetString()), "The module is installed but reported no version.");
        }
    }

    /// <summary>A client that ignores schemas must still be told everything.</summary>
    [Fact]
    public async Task Ping_says_the_same_thing_in_prose()
    {
        var result = await PnPPowerShellTools.Ping(_sessions, includeReadiness: false);
        var text = ToolResults.Text(result);
        var health = result.StructuredContent!.Value;

        Assert.Contains(health.GetProperty("version").GetString()!, text, StringComparison.Ordinal);
        Assert.Contains("Active sessions: 0", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ListSessions_returns_empty_message_when_none_exist()
    {
        var result = PnPPowerShellTools.ListSessions(_sessions);

        Assert.Contains("No sessions are currently running", ToolResults.Text(result), StringComparison.Ordinal);
        Assert.Equal(0, result.StructuredContent!.Value.GetProperty("count").GetInt32());
    }

    [Fact]
    public void ListSessions_returns_table_with_session_after_creation()
    {
        _sessions.Get("prod");
        _sessions.Get("dev");

        var result = PnPPowerShellTools.ListSessions(_sessions);
        var text = ToolResults.Text(result);

        Assert.Contains("**2** active session(s)", text, StringComparison.Ordinal);
        Assert.Contains("| prod |", text, StringComparison.Ordinal);
        Assert.Contains("| dev |", text, StringComparison.Ordinal);
        Assert.Contains("| Session |", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ListSessions_reports_each_session_in_the_structured_half()
    {
        _sessions.Get("prod");
        _sessions.Get("dev");

        var structured = PnPPowerShellTools.ListSessions(_sessions).StructuredContent!.Value;

        Assert.Equal(2, structured.GetProperty("count").GetInt32());
        Assert.Equal(2, structured.GetProperty("total").GetInt32());

        var ids = structured.GetProperty("sessions").EnumerateArray().Select(s => s.GetProperty("id").GetString()).ToList();
        Assert.Contains("prod", ids);
        Assert.Contains("dev", ids);

        Assert.All(
            structured.GetProperty("sessions").EnumerateArray(),
            s => Assert.True(
                s.GetProperty("status").GetString() is "running" or "idle" or "stopped",
                $"Unexpected status '{s.GetProperty("status").GetString()}'."));
    }
}
