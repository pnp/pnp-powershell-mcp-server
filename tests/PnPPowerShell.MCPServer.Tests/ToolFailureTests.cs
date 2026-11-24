using ModelContextProtocol.Protocol;
using PnPPowerShell.MCPServer.Services;
using System.Text.Json;

namespace PnPPowerShell.MCPServer.Tests;

public sealed class ToolFailureTests
{
    private static CallToolResult Text(string text) => new() { Content = [new TextContentBlock { Text = text }] };

    [Theory]
    [InlineData("Error: The command exceeded 10 minute(s).", true)]
    [InlineData("Blocked: read-only mode is on.", true)]
    [InlineData("Cancelled: Remove-PnPList was not confirmed, so nothing was run.", false)]
    [InlineData("Cancelled: Remove-PnPList was not explicitly confirmed, so nothing was run. Ask again and have the user tick the confirmation box.", false)]
    [InlineData("Cancelled: Remove-PnPList was not confirmed within 10 minutes, so nothing was run.", false)]
    [InlineData("Cancelled: this session was reset, restarted or ran another command since this one asked for confirmation, so nothing was run. Re-run 'pnp_run_command' and confirm again.", true)]
    [InlineData("Cancelled: this approval has expired or was already used, so nothing was run. Re-run 'pnp_run_command' and confirm the command you are shown.", true)]
    [InlineData("Command completed successfully (no output).", false)]
    [InlineData("[{\"Title\":\"Error: not a failure\"}]", false)]
    [InlineData("", false)]
    public void Only_a_failure_or_a_refusal_is_flagged(string text, bool failure) =>
        Assert.Equal(failure, ToolFailure.Mark(Text(text)).IsError is true);

    [Fact]
    public void A_structured_result_keeps_the_flag_its_tool_set()
    {
        var result = Text("Error: reported by the tool's own typed result.");
        result.StructuredContent = JsonSerializer.SerializeToElement(new { connected = false });

        Assert.NotEqual(true, ToolFailure.Mark(result).IsError);
    }

    [Fact]
    public void A_result_with_no_content_is_left_alone() =>
        Assert.NotEqual(true, ToolFailure.Mark(new CallToolResult { Content = [] }).IsError);
}
