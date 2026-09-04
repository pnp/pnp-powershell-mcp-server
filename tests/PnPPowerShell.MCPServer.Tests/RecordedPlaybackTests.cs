using PnPPowerShell.MCPServer.Services;
using PnPPowerShell.MCPServer.Tools;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>Tenant-dependent behaviour, recorded once and replayed offline.</summary>
public class RecordedPlaybackTests
{
    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "fixtures");

    /// <summary>Each case is run identically when recording and when replaying; only the source of the answer differs.</summary>
    private static readonly (string Name, Func<PowerShellSessionManager, Task<string>> Run, string Expected)[] Scenarios =
    [
        ("connect", s => Run(s, ConnectCommand()), "successfully"),
        ("web", s => Run(s, "Get-PnPWeb | Select-Object Title, Url"), "Url"),
        ("lists", s => Run(s, "Get-PnPList | Select-Object Title, ItemCount, BaseTemplate"), "Title"),
        ("status", async s => ToolResults.Text(await PnPPowerShellTools.GetPnpConnectionStatus(s)), "connected"),

        // No "search" scenario: pnp_search_commands is answered from the compiled-in corpus and never
        // reaches a session, so there is nothing to record. CommandCorpusTests cover it directly.

        // Sections 1-3 replay; section 4 reads the local machine and is covered by AuthMaterialTests.
        ("diagnose", async s => ToolResults.Text(await PnPPowerShellTools.DiagnosePnpConnection(s, null, SiteUrl())), "PnP PowerShell preflight"),

        // Failure states, each an entry in PnPErrorHints, asserted against a real message.
        ("unknown-cmdlet", s => Run(s, "Get-PnPNoSuchCmdlet9f2c"), "Find the right one with pnp_search_commands"),
        ("unknown-parameter", s => Run(s, "Get-PnPList -NoSuchParameter9f2c 'x'"), "Check the exact parameter set"),
        ("missing-list", s => Run(s, "Get-PnPList -Identity 'no-such-list-9f2c'"), "does not exist"),
        ("missing-site", s => Run(s, $"Get-PnPTenantSite -Identity '{SiteUrl()}/no-such-site-9f2c'"), "Error:"),

        // The cold start. Last, because it leaves the session with no connection.
        ("connect-no-app-registration",
            s => Run(s, "Connect-PnPOnline -Url 'https://noappreg9f2c.sharepoint.com/sites/x'"),
            "No app registration was available for that tenant"),
    ];

    public static TheoryData<string> ScenarioNames() => [.. Scenarios.Select(s => s.Name)];

    [PlaybackTheory]
    [MemberData(nameof(ScenarioNames))]
    public async Task Replays_the_recorded_answer_without_a_tenant(string name)
    {
        var scenario = Scenarios.Single(s => s.Name == name);

        Assert.True(Directory.Exists(FixtureDirectory), $"No fixtures directory at {FixtureDirectory}.");

        using var replay = new EnvVar("PNP_MCP_REPLAY_DIR", FixtureDirectory);
        await using var sessions = new PowerShellSessionManager();

        var output = await scenario.Run(sessions);

        Assert.DoesNotContain("No recorded transcript", output, StringComparison.Ordinal);
        Assert.Contains(scenario.Expected, output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The whole summarise-and-page chain, over a real recorded result set.</summary>
    [PlaybackFact]
    public async Task An_oversized_result_set_is_summarised_and_then_paged()
    {
        using var replay = new EnvVar("PNP_MCP_REPLAY_DIR", FixtureDirectory);

        // Small enough to force paging, which is the path a tenant-sized result takes at the real cap.
        using var cap = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", "2500");
        await using var sessions = new PowerShellSessionManager();

        var first = await Run(sessions, "Get-PnPList | Select-Object Title, ItemCount, BaseTemplate");

        Assert.Contains("rows, summarised", first, StringComparison.Ordinal);
        Assert.Contains("Fields: Title, ItemCount, BaseTemplate", first, StringComparison.Ordinal);
        Assert.DoesNotContain(OutputLimit.TruncationMarker, first, StringComparison.Ordinal);

        var cursor = sessions.Get(null).Held!.Cursor;
        Assert.Contains($"cursor '{cursor}'", first, StringComparison.Ordinal);

        var second = ToolResults.Text(PnPPowerShellTools.GetPnpResultPage(sessions, cursor, NextOffset(first)));
        Assert.Contains("Rows ", second, StringComparison.Ordinal);
        Assert.DoesNotContain("No held result set", second, StringComparison.Ordinal);

        // The cursor belongs to the session, so the next command there invalidates it.
        await Run(sessions, "Get-PnPWeb | Select-Object Title, Url");
        Assert.Contains("No held result set", ToolResults.Text(PnPPowerShellTools.GetPnpResultPage(sessions, cursor)), StringComparison.Ordinal);
    }

    private static int NextOffset(string page) =>
        int.Parse(System.Text.RegularExpressions.Regex.Match(page, @"offset (\d+)").Groups[1].Value);

    [PlaybackFact]
    public async Task Recorded_fixtures_carry_no_tenant_identity()
    {
        foreach (var fixture in Directory.GetFiles(FixtureDirectory, "*.transcript"))
        {
            var content = File.ReadAllText(fixture);
            var name = Path.GetFileName(fixture);

            // The scrubber's own placeholders are the only tenant and identity shapes allowed through.
            foreach (var host in Hosts(content))
            {
                Assert.True(
                    host is "contoso" or "fabrikam" or "northwind" or "adventureworks" or "contoso-admin" or "contoso-my",
                    $"{name} names an unscrubbed tenant host '{host}'.");
            }

            Assert.DoesNotContain("eyJ", content, StringComparison.Ordinal);
        }

        await Task.CompletedTask;
    }

    /// <summary>Records every scenario against a live tenant. Only runs when explicitly asked to.</summary>
    [RecordingFact]
    public async Task Record()
    {
        var target = Directory.CreateDirectory(SourceFixtureDirectory());

        using var record = new EnvVar("PNP_MCP_RECORD_DIR", target.FullName);
        await using var sessions = new PowerShellSessionManager();

        foreach (var (name, run, _) in Scenarios)
        {
            var output = await run(sessions);
            Assert.False(
                output.Contains("Could not launch 'pwsh'", StringComparison.Ordinal) ||
                output.Contains("module is not installed", StringComparison.Ordinal),
                $"Scenario '{name}' could not reach PnP PowerShell: {output}");
        }
    }

    // Playback passes the placeholders the scrubber produces, so both land on the same fixture key.
    private static string SiteUrl() =>
        Environment.GetEnvironmentVariable("PNP_MCP_RECORD_TENANT_URL") ?? "https://contoso.sharepoint.com/sites/contoso";

    private static string ConnectCommand() =>
        $"Connect-PnPOnline -Url '{SiteUrl()}' " +
        $"-ClientId '{Environment.GetEnvironmentVariable("PNP_MCP_RECORD_CLIENT_ID") ?? "00000000-0000-4000-8000-000000000001"}' -PersistLogin";

    private static Task<string> Run(PowerShellSessionManager sessions, string command) =>
        PnPPowerShellTools.RunPnpCommand(sessions, server: null!, context: null!, command);

    /// <summary>The fixtures under source control, rather than the copy in the test output directory.</summary>
    private static string SourceFixtureDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PnPPowerShell.MCPServer.Tests.csproj")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(
            directory?.FullName ?? throw new InvalidOperationException("Could not locate the test project directory."),
            "fixtures");
    }

    private static IEnumerable<string> Hosts(string content) =>
        System.Text.RegularExpressions.Regex
            .Matches(content, @"(?i)\b([a-z0-9][a-z0-9-]*)\.(sharepoint|onmicrosoft)\.com\b")
            .Select(m => m.Groups[1].Value.ToLowerInvariant())
            .Distinct();
}
