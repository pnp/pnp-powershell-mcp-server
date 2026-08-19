using PnPPowerShell.MCPServer.Services;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>Drives a real pwsh session; skipped when pwsh or PnP.PowerShell is unavailable.</summary>
public class PowerShellSessionTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromMinutes(3);

    // Bounded: a command that fails immediately must not leave the test spinning forever.
    private static async Task WaitUntilBusy(PowerShellSession session)
    {
        for (var i = 0; i < 200 && !session.IsBusy; i++)
        {
            await Task.Delay(50);
        }

        Assert.True(session.IsBusy, "The wedged command never took the session.");
    }

    [RequiresPnPFact]
    public async Task State_survives_across_calls()
    {
        // The reason the session exists: PnP holds its connection in process memory, so a
        // process-per-call model silently dropped it.
        await using var session = new PowerShellSession("test-persist");

        await session.ExecuteAsync("$probe = 'KEEP-ME'", Generous);
        var result = await session.ExecuteAsync("\"probe=[$probe]\"", Generous);

        Assert.Contains("probe=[KEEP-ME]", result);
    }

    [RequiresPnPFact]
    public async Task The_wrapper_leaves_only_the_variable_it_is_currently_using()
    {
        await using var session = new PowerShellSession("test-cleanup");

        await session.ExecuteAsync("'first'", Generous);

        // $__pnpScript necessarily holds the script being run, so asserting on its contents proves
        // nothing. Enumerating the __pnp namespace does: anything the previous command left behind
        // would show up here as an extra name.
        var names = await session.ExecuteAsync(
            "(Get-Variable -Name '__pnp*' -ErrorAction SilentlyContinue | ForEach-Object Name | Sort-Object) -join ','",
            Generous);

        Assert.Contains("__pnpScript", names);
        Assert.DoesNotContain("__pnpCommandText", names);
        Assert.DoesNotContain("__pnpCommandResult", names);
        Assert.DoesNotContain("__pnpFound", names);
    }

    [RequiresPnPFact]
    public async Task A_failing_command_is_reported_as_an_error()
    {
        await using var session = new PowerShellSession("test-failure");

        var result = await session.ExecuteAsync("throw 'deliberate failure'", Generous);

        Assert.StartsWith("Error:", result);
        Assert.Contains("deliberate failure", result);
    }

    [RequiresPnPFact]
    public async Task A_runaway_command_is_terminated_and_the_session_recovers()
    {
        await using var session = new PowerShellSession("test-timeout");

        await session.ExecuteAsync("$before = 'SET'", Generous);

        var timedOut = await session.ExecuteAsync("Start-Sleep -Seconds 120", TimeSpan.FromSeconds(10));
        Assert.Contains("exceeded", timedOut);
        Assert.Contains("second", timedOut);

        // A fresh process starts on the next call, so in-session state is gone but the session works.
        var after = await session.ExecuteAsync("\"after=[$before]\"", Generous);
        Assert.Contains("after=[]", after);
    }

    [RequiresPnPFact]
    public async Task Reset_is_not_blocked_by_a_command_holding_the_session()
    {
        await using var session = new PowerShellSession("test-reset");

        await session.ExecuteAsync("'warm'", Generous);

        var wedged = session.ExecuteAsync("Start-Sleep -Seconds 120", Generous);
        await WaitUntilBusy(session);

        // Recovery must not wait on the gate the wedged command is holding.
        var start = DateTimeOffset.UtcNow;
        await session.ResetAsync();
        var elapsed = DateTimeOffset.UtcNow - start;

        Assert.True(elapsed < TimeSpan.FromSeconds(15), $"ResetAsync took {elapsed}");
        Assert.Contains("Error:", await wedged);
        Assert.Contains("recovered", await session.ExecuteAsync("'recovered'", Generous));
    }

    [RequiresPnPFact]
    public async Task Sessions_are_isolated_from_each_other()
    {
        await using var manager = new PowerShellSessionManager();

        await manager.Get("tenant-a").ExecuteAsync("$who = 'A'", Generous);
        var b = await manager.Get("tenant-b").ExecuteAsync("\"who=[$who]\"", Generous);

        Assert.Contains("who=[]", b);
    }

    [RequiresPnPFact]
    public async Task An_unnamed_session_resolves_to_the_default()
    {
        await using var manager = new PowerShellSessionManager();

        await manager.Get(null).ExecuteAsync("$marker = 'DEFAULT'", Generous);
        var same = await manager.Get(PowerShellSessionManager.DefaultSessionId).ExecuteAsync("\"marker=[$marker]\"", Generous);

        Assert.Contains("marker=[DEFAULT]", same);
    }

    [RequiresPnPFact]
    public async Task A_busy_session_reports_itself_rather_than_waiting_without_limit()
    {
        await using var session = new PowerShellSession("test-busy");

        await session.ExecuteAsync("'warm'", Generous);

        var wedged = session.ExecuteAsync("Start-Sleep -Seconds 60", Generous);
        await WaitUntilBusy(session);

        var queued = await session.ExecuteAsync("'queued'", TimeSpan.FromSeconds(3));
        Assert.Contains("busy", queued);

        await session.ResetAsync();
        await wedged;
    }
}
