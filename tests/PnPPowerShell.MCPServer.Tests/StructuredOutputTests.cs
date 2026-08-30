using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PnPPowerShell.MCPServer.Models;
using PnPPowerShell.MCPServer.Services;
using PnPPowerShell.MCPServer.Tools;
using System.Text.Json;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>
/// The contract every structured tool shares: a real output schema in the catalogue, a payload that
/// matches it, prose that says the same thing, and both halves inside one output budget.
/// </summary>
public class StructuredOutputTests
{
    /// <summary>Tools converted under roadmap #12. Anything not here still returns prose by choice.</summary>
    public static TheoryData<string> StructuredTools() =>
    [
        "pnp_search_commands",
        "pnp_ping",
        "pnp_list_sessions",
        "pnp_get_result_page",
        "pnp_get_connection_status",
    ];

    /// <summary>A string-returning tool yields a useless schema; that is the trap the item names.</summary>
    [Theory]
    [MemberData(nameof(StructuredTools))]
    public void The_tool_advertises_an_object_output_schema(string name)
    {
        var tool = ToolCatalog.All.Single(t => t.ProtocolTool.Name == name);

        Assert.True(tool.ProtocolTool.OutputSchema.HasValue, $"{name} advertises no output schema.");

        var schema = tool.ProtocolTool.OutputSchema!.Value;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.True(
            schema.GetProperty("properties").EnumerateObject().Any(),
            $"{name}'s schema describes no properties.");
    }

    /// <summary>Every converted tool must still be annotated, since clients gate auto-approval on that.</summary>
    [Theory]
    [MemberData(nameof(StructuredTools))]
    public void The_tool_keeps_its_annotations(string name)
    {
        var annotations = ToolCatalog.All.Single(t => t.ProtocolTool.Name == name).ProtocolTool.Annotations;

        Assert.NotNull(annotations);
        Assert.NotNull(annotations.ReadOnlyHint);
        Assert.NotNull(annotations.OpenWorldHint);
    }

    [Fact]
    public async Task Every_structured_tool_answers_with_both_halves_inside_the_cap()
    {
        await using var sessions = new PowerShellSessionManager();

        // A directory that holds no transcripts, so nothing here reaches a tenant.
        var empty = Directory.CreateTempSubdirectory("pnp-structured");

        try
        {
            using var replay = new EnvVar("PNP_MCP_REPLAY_DIR", empty.FullName);

            CallToolResult[] results =
            [
                PnPPowerShellTools.SearchPnpCommands("site", 20),
                PnPPowerShellTools.Ping(sessions),
                PnPPowerShellTools.ListSessions(sessions),
                PnPPowerShellTools.GetPnpResultPage(sessions, "no-such-cursor"),
                await PnPPowerShellTools.GetPnpConnectionStatus(sessions),
            ];

            Assert.All(results, r =>
            {
                var text = ToolResults.Text(r);
                var json = r.StructuredContent is { } s ? s.GetRawText() : string.Empty;

                Assert.NotEmpty(text);
                Assert.True(
                    text.Length + json.Length <= OutputLimit.MaxChars,
                    $"text {text.Length} + json {json.Length} exceeds the {OutputLimit.MaxChars} cap.");
            });
        }
        finally
        {
            empty.Delete(recursive: true);
        }
    }

    /// <summary>Ping used to hand-build this JSON into a string; the shape must survive the conversion.</summary>
    [Fact]
    public async Task Health_round_trips_through_the_source_generated_context()
    {
        await using var sessions = new PowerShellSessionManager();

        var json = PnPPowerShellTools.Ping(sessions).StructuredContent!.Value.GetRawText();
        var health = JsonSerializer.Deserialize(json, ToolOutputJsonContext.Default.ServerHealth);

        Assert.NotNull(health);
        Assert.Equal("ok", health.Status);
        Assert.Equal(0, health.ActiveSessions);
    }

    /// <summary>An error path carries no payload to type, and must say so rather than invent one.</summary>
    [Fact]
    public async Task An_unknown_cursor_is_an_error_with_no_structured_payload()
    {
        await using var sessions = new PowerShellSessionManager();

        var result = PnPPowerShellTools.GetPnpResultPage(sessions, "no-such-cursor");

        Assert.True(result.IsError);
        Assert.Null(result.StructuredContent);
        Assert.Contains("No held result set", ToolResults.Text(result), StringComparison.Ordinal);
    }

    /// <summary>Paging offsets in the structured half must match the rows the text half actually shows.</summary>
    [Fact]
    public void The_result_page_reports_offsets_that_match_what_it_rendered()
    {
        var held = ResultSummary.TryCapture(
            "[" + string.Join(",", Enumerable.Range(0, 400).Select(i => $"{{\"Title\":\"Row {i}\",\"Index\":{i}}}")) + "]");

        Assert.NotNull(held);

        var (start, end, pageable, _) = ResultSummary.Paging(held, 0);

        Assert.Equal(0, start);
        Assert.True(end > 0 && end <= pageable);
        Assert.Equal(held.Rows.Count, pageable);
    }
}
