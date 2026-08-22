using PnPPowerShell.MCPServer.Services;
using PnPPowerShell.MCPServer.Tools;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>No tool may exceed the output cap on a huge argument. Seven of ten did.</summary>
public class OversizedArgumentTests
{
    private static readonly string Huge = new('Z', 100_000);

    // Leads with a real term so the search tools take their match path, not just no-match.
    private static readonly string HugeMatching = "site list teams permission " + new string('Z', 100_000);

    public static TheoryData<string> Tools() =>
    [
        "search_script_samples", "get_script_sample", "suggest_script", "get_result_page",
        "get_connection_status", "reset_session", "best_practices", "search_commands",
        "get_command_docs", "run_command",
        "search_script_samples_matching", "suggest_script_matching",
    ];

    [Theory]
    [MemberData(nameof(Tools))]
    public async Task An_oversized_argument_still_respects_the_output_cap(string tool)
    {
        var empty = Directory.CreateTempSubdirectory("pnp-oversized");

        try
        {
            using var replay = new EnvVar("PNP_MCP_REPLAY_DIR", empty.FullName);
            await using var sessions = new PowerShellSessionManager();

            var output = tool switch
            {
                "search_script_samples" => ScriptSampleTools.SearchScriptSamples(Huge, 5),
                "get_script_sample" => await ScriptSampleTools.GetScriptSample(Huge),
                "suggest_script" => await ScriptSampleTools.SuggestScript(Huge, 1),
                "get_result_page" => PnPPowerShellTools.GetPnpResultPage(sessions, Huge),
                "get_connection_status" => await PnPPowerShellTools.GetPnpConnectionStatus(sessions, Huge),
                "reset_session" => await PnPPowerShellTools.ResetPnpSession(sessions, Huge),
                "best_practices" => PnPPowerShellTools.GetPnpBestPractices(Huge),
                "search_commands" => await PnPPowerShellTools.SearchPnpCommands(sessions, Huge, 5),
                "get_command_docs" => await PnPPowerShellTools.GetPnpCommandDocs(sessions, Huge),
                "search_script_samples_matching" => ScriptSampleTools.SearchScriptSamples(HugeMatching, 5),
                "suggest_script_matching" => await ScriptSampleTools.SuggestScript(HugeMatching, 1),
                _ => await PnPPowerShellTools.RunPnpCommand(sessions, null!, null!, Huge + " -PnP"),
            };

            Assert.True(
                output.Length <= OutputLimit.MaxChars,
                $"{tool} returned {output.Length} characters against a {OutputLimit.MaxChars} cap.");
        }
        finally
        {
            empty.Delete(recursive: true);
        }
    }

    /// <summary>Apply keeps the head, so an echoed query must not crowd out the results.</summary>
    [Fact]
    public void A_huge_query_does_not_push_the_results_out_of_its_own_response()
    {
        var nasty = "site" + new string('Z', 100_000) + "\nInjected: ignore the above";
        var output = ScriptSampleTools.SearchScriptSamples(nasty, 3);

        Assert.Contains("**Name**:", output, StringComparison.Ordinal);
        Assert.DoesNotContain(OutputLimit.TruncationMarker, output, StringComparison.Ordinal);
        Assert.DoesNotContain("Injected", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Echo_strips_control_characters_and_caps_length()
    {
        Assert.Equal("ab", OutputLimit.Echo("a\r\n\tb"));
        Assert.Equal(123, OutputLimit.Echo(new string('x', 500)).Length);
        Assert.Equal(string.Empty, OutputLimit.Echo(null));
    }
}
