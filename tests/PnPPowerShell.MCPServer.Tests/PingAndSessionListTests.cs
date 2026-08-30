using System.Text.Json;
using PnPPowerShell.MCPServer.Services;
using PnPPowerShell.MCPServer.Tools;

namespace PnPPowerShell.MCPServer.Tests;

public sealed class PingAndSessionListTests : IAsyncDisposable
{
    private readonly PowerShellSessionManager _sessions = new();

    public async ValueTask DisposeAsync() => await _sessions.DisposeAsync();

    [Fact]
    public async Task Ping_returns_valid_json_with_expected_keys()
    {
        var result = await PnPPowerShellTools.Ping(_sessions, includeReadiness: false);

        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;

        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("version").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("uptime").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("startedUtc").GetString()));
        Assert.True(root.TryGetProperty("readOnlyMode", out _));
        Assert.True(root.TryGetProperty("activeSessions", out _));
    }

    [Fact]
    public async Task Ping_reports_zero_sessions_on_a_fresh_manager()
    {
        var result = await PnPPowerShellTools.Ping(_sessions, includeReadiness: false);
        var doc = JsonDocument.Parse(result);

        Assert.Equal(0, doc.RootElement.GetProperty("activeSessions").GetInt32());
    }

    [Fact]
    public async Task Ping_reports_active_sessions_after_one_is_created()
    {
        _sessions.Get("alpha");

        var result = await PnPPowerShellTools.Ping(_sessions, includeReadiness: false);
        var doc = JsonDocument.Parse(result);

        Assert.Equal(1, doc.RootElement.GetProperty("activeSessions").GetInt32());
    }

    [Fact]
    public async Task Ping_reflects_readonly_mode()
    {
        using var _ = new EnvVar("PNP_MCP_READONLY", "true");

        var result = await PnPPowerShellTools.Ping(_sessions, includeReadiness: false);
        var doc = JsonDocument.Parse(result);

        Assert.True(doc.RootElement.GetProperty("readOnlyMode").GetBoolean());
    }

    [Fact]
    public async Task Ping_without_readiness_omits_the_environment_fields()
    {
        var result = await PnPPowerShellTools.Ping(_sessions, includeReadiness: false);
        var root = JsonDocument.Parse(result).RootElement;

        Assert.False(root.TryGetProperty("pwshAvailable", out _));
        Assert.False(root.TryGetProperty("pnpModuleInstalled", out _));
    }

    [Fact]
    public async Task Ping_with_readiness_reports_pwsh_and_module_state_as_booleans()
    {
        var result = await PnPPowerShellTools.Ping(_sessions, includeReadiness: true);
        var root = JsonDocument.Parse(result).RootElement;

        // Values depend on the machine; the contract is that the keys exist and are typed.
        Assert.True(root.TryGetProperty("pwshAvailable", out var pwsh));
        Assert.True(pwsh.ValueKind is JsonValueKind.True or JsonValueKind.False);
        Assert.True(root.TryGetProperty("pnpModuleInstalled", out var installed));
        Assert.True(installed.ValueKind is JsonValueKind.True or JsonValueKind.False);
        Assert.True(root.TryGetProperty("pnpModuleVersion", out _));
    }

    [Fact]
    public void ListSessions_returns_empty_message_when_none_exist()
    {
        var result = PnPPowerShellTools.ListSessions(_sessions);

        Assert.Contains("No sessions are currently running", result);
    }

    [Fact]
    public void ListSessions_returns_table_with_session_after_creation()
    {
        _sessions.Get("prod");
        _sessions.Get("dev");

        var result = PnPPowerShellTools.ListSessions(_sessions);

        Assert.Contains("**2** active session(s)", result);
        Assert.Contains("| prod |", result);
        Assert.Contains("| dev |", result);
        Assert.Contains("| Session |", result);
    }
}
