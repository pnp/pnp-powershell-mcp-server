using PnPPowerShell.MCPServer.Models;
using PnPPowerShell.MCPServer.Services;
using PnPPowerShell.MCPServer.Tools;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>
/// Where the prose and the structured payload are produced by separate code, they can disagree without
/// anything failing. These assert they do not — the risk structured output introduces that prose did not.
/// </summary>
public partial class HalvesAgreeTests(ITestOutputHelper output)
{
    [GeneratedRegex(@"offset (\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex MoreOffset();

    private static HeldResultSet Held(int rows) =>
        ResultSummary.TryCapture(
            "[" + string.Join(",", Enumerable.Range(0, rows).Select(i => $"{{\"Title\":\"Row {i}\",\"Index\":{i}}}")) + "]")
        ?? throw new InvalidOperationException("The sample result set was not capturable.");

    /// <summary>
    /// The offset a client reads from the structured half must be the one the prose tells a human to use.
    /// The two are rendered by different code from the same held set, so nothing else forces them to match.
    /// </summary>
    [Theory]
    [InlineData(400, 50_000)]
    [InlineData(400, 5_000)]
    [InlineData(400, 2_000)]
    [InlineData(40, 50_000)]
    public async Task The_next_offset_matches_the_one_printed_in_the_page(int rows, int cap)
    {
        using var capped = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", cap.ToString());
        await using var sessions = new PowerShellSessionManager();

        var session = sessions.Get(null);
        session.Held = Held(rows);

        var result = PnPPowerShellTools.GetPnpResultPage(sessions, session.Held!.Cursor);
        var text = ToolResults.Text(result);
        var page = JsonSerializer.Deserialize(result.StructuredContent!.Value.GetRawText(), ToolOutputJsonContext.Default.ResultPage);

        Assert.NotNull(page);
        output.WriteLine($"rows={rows} cap={cap} -> offset={page.Offset} next={page.NextOffset} pageable={page.PageableRows}");

        var printed = MoreOffset().Match(text);

        if (page.NextOffset is { } next)
        {
            Assert.True(printed.Success, $"The structured half offers offset {next} but the page prints no MORE line.");
            Assert.Equal(next, int.Parse(printed.Groups[1].Value));
        }
        else
        {
            Assert.False(printed.Success, $"The page prints a MORE line at offset {printed.Groups[1].Value} but the structured half says there is no next page.");
        }
    }

    /// <summary>Paging to the reported next offset must actually advance, or a client loops forever.</summary>
    [Fact]
    public async Task Following_the_reported_offset_terminates()
    {
        using var capped = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", "2500");
        await using var sessions = new PowerShellSessionManager();

        var session = sessions.Get(null);
        session.Held = Held(400);
        var cursor = session.Held!.Cursor;

        var offset = 0;
        var pages = 0;

        while (pages++ < 500)
        {
            var page = JsonSerializer.Deserialize(
                PnPPowerShellTools.GetPnpResultPage(sessions, cursor, offset).StructuredContent!.Value.GetRawText(),
                ToolOutputJsonContext.Default.ResultPage)!;

            if (page.NextOffset is not { } next)
            {
                break;
            }

            Assert.True(next > offset, $"Paging did not advance: offset {offset} reported next {next}.");
            offset = next;
        }

        Assert.True(pages < 500, "Paging never reached the end of the held rows.");
        output.WriteLine($"walked the whole set in {pages} pages");
    }

    /// <summary>An async tool returning structured content is a different SDK path from a sync one.</summary>
    [Fact]
    public async Task The_async_structured_tool_reports_a_connected_session_faithfully()
    {
        var directory = Directory.CreateTempSubdirectory("pnp-status-ok");

        try
        {
            const string connected =
                """{"connected":true,"url":"https://contoso.sharepoint.com/sites/x","tenantAdminUrl":"https://contoso-admin.sharepoint.com","connectionType":"O365","account":"someone@contoso.com"}""";

            var key = SessionTranscript.Key(string.Empty, "connection-status");
            File.WriteAllText(Path.Combine(directory.FullName, key + ".transcript"), $"# key: {key}\n\n--- output ---\n{connected}");

            using var replay = new EnvVar("PNP_MCP_REPLAY_DIR", directory.FullName);
            await using var sessions = new PowerShellSessionManager();

            var result = await PnPPowerShellTools.GetPnpConnectionStatus(sessions);
            var status = JsonSerializer.Deserialize(
                result.StructuredContent!.Value.GetRawText(),
                ToolOutputJsonContext.Default.ConnectionStatus);

            Assert.NotNull(status);
            Assert.True(status.Connected);
            Assert.Equal("https://contoso.sharepoint.com/sites/x", status.Url);
            Assert.Equal("someone@contoso.com", status.Account);
            Assert.Equal(PowerShellSessionManager.DefaultSessionId, status.SessionId);

            // The prose must carry the same facts, for a client that ignores schemas.
            var text = ToolResults.Text(result);
            Assert.Contains("contoso.sharepoint.com", text, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>The structured half of connection status is not passed through OutputLimit.</summary>
    [Fact]
    public async Task A_pathological_connection_payload_stays_bounded()
    {
        var directory = Directory.CreateTempSubdirectory("pnp-status-long");

        try
        {
            var url = "https://contoso.sharepoint.com/sites/" + new string('u', 120_000);
            var payload = $$"""{"connected":true,"url":"{{url}}","connectionType":"O365"}""";

            var key = SessionTranscript.Key(string.Empty, "connection-status");
            File.WriteAllText(Path.Combine(directory.FullName, key + ".transcript"), $"# key: {key}\n\n--- output ---\n{payload}");

            using var replay = new EnvVar("PNP_MCP_REPLAY_DIR", directory.FullName);
            await using var sessions = new PowerShellSessionManager();

            var result = await PnPPowerShellTools.GetPnpConnectionStatus(sessions);
            var text = ToolResults.Text(result);
            var json = result.StructuredContent is { } s ? s.GetRawText() : string.Empty;

            output.WriteLine($"text={text.Length} json={json.Length} cap={OutputLimit.MaxChars}");

            Assert.True(
                text.Length + json.Length <= OutputLimit.MaxChars,
                $"text {text.Length} + json {json.Length} exceeds the {OutputLimit.MaxChars} cap.");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
