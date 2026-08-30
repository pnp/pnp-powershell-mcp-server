using PnPPowerShell.MCPServer.Services;
using PnPPowerShell.MCPServer.Tools;

namespace PnPPowerShell.MCPServer.Tests;

public sealed class PingAndSessionListTests : IAsyncDisposable
{
    private readonly PowerShellSessionManager _sessions = new();

    public async ValueTask DisposeAsync() => await _sessions.DisposeAsync();

    // Both tools used to hand-build their payload into a string; the assertions now read the structured
    // half, which is the same data the client reads rather than a rendering of it.
    [Fact]
    public void Ping_reports_the_expected_fields()
    {
        var health = PnPPowerShellTools.Ping(_sessions).StructuredContent!.Value;

        Assert.Equal("ok", health.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(health.GetProperty("version").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(health.GetProperty("packageVersion").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(health.GetProperty("uptime").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(health.GetProperty("startedUtc").GetString()));
        Assert.True(health.TryGetProperty("readOnlyMode", out _));
        Assert.True(health.TryGetProperty("activeSessions", out _));
    }

    [Fact]
    public void Ping_reports_zero_sessions_on_a_fresh_manager() =>
        Assert.Equal(0, PnPPowerShellTools.Ping(_sessions).StructuredContent!.Value.GetProperty("activeSessions").GetInt32());

    [Fact]
    public void Ping_reports_active_sessions_after_one_is_created()
    {
        _sessions.Get("alpha");

        Assert.Equal(1, PnPPowerShellTools.Ping(_sessions).StructuredContent!.Value.GetProperty("activeSessions").GetInt32());
    }

    [Fact]
    public void Ping_reflects_readonly_mode()
    {
        using var _ = new EnvVar("PNP_MCP_READONLY", "true");

        Assert.True(PnPPowerShellTools.Ping(_sessions).StructuredContent!.Value.GetProperty("readOnlyMode").GetBoolean());
    }

    /// <summary>A client that ignores schemas must still be told everything.</summary>
    [Fact]
    public void Ping_says_the_same_thing_in_prose()
    {
        var result = PnPPowerShellTools.Ping(_sessions);
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
