using ModelContextProtocol.Protocol;
using PnPPowerShell.MCPServer.Models;
using PnPPowerShell.MCPServer.Services;
using PnPPowerShell.MCPServer.Tools;
using System.Text.Json;
using Xunit.Abstractions;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>Attempts to break the tools converted under #12, rather than confirm they work.</summary>
public class StructuredAdversarialTests(ITestOutputHelper output)
{
    /// <summary>A session-level failure must not be reported as a connection fact.</summary>
    // ParseStatus slices between the first '{' and last '}'. An error message containing braces would
    // otherwise deserialize to an all-defaults object, i.e. a confident "connected: false".
    [Theory]
    [InlineData("Error: the session died")]
    [InlineData("Error: unexpected token {oops} in module {PnP.PowerShell}")]
    [InlineData("Error: {\"unrelated\":true}")]
    [InlineData("{ not json at all")]
    [InlineData("")]
    public async Task A_session_failure_is_not_reported_as_a_disconnected_tenant(string failure)
    {
        var directory = Directory.CreateTempSubdirectory("pnp-status");

        try
        {
            var key = SessionTranscript.Key(string.Empty, "connection-status");
            File.WriteAllText(Path.Combine(directory.FullName, key + ".transcript"), $"# key: {key}\n\n--- output ---\n{failure}");

            using var replay = new EnvVar("PNP_MCP_REPLAY_DIR", directory.FullName);
            await using var sessions = new PowerShellSessionManager();

            var result = await PnPPowerShellTools.GetPnpConnectionStatus(sessions);

            output.WriteLine($"'{failure}' -> structured={(result.StructuredContent is null ? "none" : result.StructuredContent.Value.GetRawText())}");

            // Asserted outright, not conditionally: an earlier version of this test allowed a payload as
            // long as connected was false, which is exactly the wrong answer a failed probe produces.
            // "Error: {\"unrelated\":true}" deserialized to an all-defaults record and stated
            // connected:false as a fact about the tenant.
            Assert.True(
                result.StructuredContent is null,
                $"A failed status probe produced a payload: {result.StructuredContent?.GetRawText()}");

            Assert.NotEmpty(ToolResults.Text(result));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Every structured tool's error path: a declared schema with no payload must still be usable.</summary>
    [Fact]
    public async Task Error_paths_are_flagged_and_carry_no_invented_payload()
    {
        await using var sessions = new PowerShellSessionManager();

        CallToolResult[] errors =
        [
            PnPPowerShellTools.SearchPnpCommands("   ", 5),
            PnPPowerShellTools.GetPnpResultPage(sessions, "no-such-cursor"),
        ];

        Assert.All(errors, r =>
        {
            Assert.True(r.IsError, "An error path did not set isError.");
            Assert.Null(r.StructuredContent);
            Assert.NotEmpty(ToolResults.Text(r));
        });
    }

    /// <summary>Connection status was never capped, and now emits a second copy of its payload.</summary>
    [Fact]
    public async Task A_huge_session_failure_does_not_blow_the_cap()
    {
        var directory = Directory.CreateTempSubdirectory("pnp-status-big");

        try
        {
            var failure = "Error: the command failed\n\nOutput before the failure:\n" + new string('x', 200_000);
            var key = SessionTranscript.Key(string.Empty, "connection-status");
            File.WriteAllText(Path.Combine(directory.FullName, key + ".transcript"), $"# key: {key}\n\n--- output ---\n{failure}");

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

    /// <summary>FitToCap over an empty list must render the empty case, not shrink below zero.</summary>
    [Fact]
    public async Task Listing_no_sessions_returns_the_empty_message()
    {
        await using var sessions = new PowerShellSessionManager();

        var result = PnPPowerShellTools.ListSessions(sessions);

        Assert.Equal(0, result.StructuredContent!.Value.GetProperty("count").GetInt32());
        Assert.Contains("No sessions", ToolResults.Text(result), StringComparison.Ordinal);
    }

    /// <summary>A session id is caller-supplied and reaches a markdown table and a JSON string.</summary>
    [Theory]
    [InlineData("a|b")]
    [InlineData("a\nb")]
    [InlineData("a\\b")]
    [InlineData("\"quoted\"")]
    public async Task A_hostile_session_id_survives_both_halves(string id)
    {
        await using var sessions = new PowerShellSessionManager();
        sessions.Get(id);

        var result = PnPPowerShellTools.ListSessions(sessions);
        var text = ToolResults.Text(result);
        var structured = result.StructuredContent!.Value;

        // The table must not gain a column. Counted on unescaped pipes only: a literal pipe in an id is
        // written as "\|", which markdown renders inside the cell rather than as a new column.
        var header = text.Split('\n').First(l => l.StartsWith("| Session", StringComparison.Ordinal));
        var rows = text.Split('\n').Where(l =>
            l.StartsWith("| ", StringComparison.Ordinal) &&
            !l.StartsWith("| Session", StringComparison.Ordinal) &&
            !l.StartsWith("|---", StringComparison.Ordinal));

        Assert.All(rows, r => Assert.Equal(Columns(header), Columns(r)));

        var parsed = JsonSerializer.Deserialize(structured.GetRawText(), ToolOutputJsonContext.Default.SessionListResult);
        Assert.NotNull(parsed);
        Assert.Single(parsed.Sessions);
    }

    /// <summary>Cell count of a markdown row, ignoring pipes the renderer escaped into a cell.</summary>
    private static int Columns(string row)
    {
        var cells = 0;

        for (var i = 0; i < row.Length; i++)
        {
            if (row[i] == '|' && (i == 0 || row[i - 1] != '\\'))
            {
                cells++;
            }
        }

        return cells;
    }

    /// <summary>Paging offsets must agree with the rows the text half actually rendered.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-10)]
    [InlineData(int.MaxValue)]
    public void Reported_offsets_stay_inside_the_held_rows(int offset)
    {
        var held = ResultSummary.TryCapture(
            "[" + string.Join(",", Enumerable.Range(0, 300).Select(i => $"{{\"Title\":\"Row {i}\"}}")) + "]");

        Assert.NotNull(held);

        var (start, end, pageable, _) = ResultSummary.Paging(held, offset);

        Assert.InRange(start, 0, Math.Max(pageable - 1, 0));
        Assert.InRange(end, start, pageable);
        Assert.Equal(held.Rows.Count, pageable);
    }
}
