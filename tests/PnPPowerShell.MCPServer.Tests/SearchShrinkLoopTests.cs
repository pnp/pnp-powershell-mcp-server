using PnPPowerShell.MCPServer.Services;
using PnPPowerShell.MCPServer.Tools;
using Xunit.Abstractions;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>
/// The shrink-to-fit loop in pnp_search_commands, attacked directly. It is the newest and most
/// intricate code in the search path: it rewrites its own input until two independently rendered
/// halves fit one budget, and it already shipped one non-terminating case.
/// </summary>
public class SearchShrinkLoopTests(ITestOutputHelper output)
{
    // Every shape that changes which branch the loop takes: no match, one match, an exact name, a
    // cmdlet with ~200 parameters, a query longer than the whole cap, and one the tokenizer drops.
    public static TheoryData<string> Queries() =>
    [
        "",
        " ",
        "Z",
        new string('Z', 100_000),
        "site list teams permission " + new string('Z', 100_000),
        "site",
        "site list file user group set get add remove",
        "Get-PnPWeb",
        "Set-PnPTenant",
        "Add-PnPAzureADGroupMember",
        "サイトを作成する",
        "!!! ??? ...",
    ];

    /// <summary>The invariant the loop exists to hold: both halves together, inside the cap.</summary>
    [Theory]
    [MemberData(nameof(Queries))]
    public void Both_halves_together_fit_every_cap(string query)
    {
        foreach (var cap in new[] { OutputLimit.MinimumMaxChars, 2_500, 5_000, 12_000, 50_000 })
        {
            using var capped = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", cap.ToString());

            foreach (var limit in new[] { 1, 2, 5, 20, 100 })
            {
                var result = PnPPowerShellTools.SearchPnpCommands(query, limit);
                var text = ToolResults.Text(result);
                var json = result.StructuredContent is { } s ? s.GetRawText() : string.Empty;

                Assert.True(
                    text.Length + json.Length <= cap,
                    $"cap={cap} limit={limit} query='{Describe(query)}': text {text.Length} + json {json.Length} = {text.Length + json.Length}.");
            }
        }
    }

    /// <summary>Termination, asserted with a clock: the failure mode of this loop is a hang, not a wrong answer.</summary>
    [Theory]
    [MemberData(nameof(Queries))]
    public async Task Every_query_shape_returns_promptly(string query)
    {
        foreach (var cap in new[] { OutputLimit.MinimumMaxChars, 5_000, 50_000 })
        {
            using var capped = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", cap.ToString());

            foreach (var limit in new[] { 1, 3, 100 })
            {
                var captured = (Query: query, Limit: limit);
                var search = Task.Run(() => PnPPowerShellTools.SearchPnpCommands(captured.Query, captured.Limit));
                var finished = await Task.WhenAny(search, Task.Delay(TimeSpan.FromSeconds(15)));

                Assert.True(
                    ReferenceEquals(finished, search),
                    $"cap={cap} limit={limit} query='{Describe(query)}' did not return within 15 seconds.");
            }
        }
    }

    /// <summary>Shrinking must never let the two halves describe different result sets.</summary>
    [Theory]
    [MemberData(nameof(Queries))]
    public void The_two_halves_never_disagree(string query)
    {
        foreach (var cap in new[] { OutputLimit.MinimumMaxChars, 5_000, 50_000 })
        {
            using var capped = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", cap.ToString());

            var result = PnPPowerShellTools.SearchPnpCommands(query, 100);
            if (result.StructuredContent is not { } structured)
            {
                continue;
            }

            var text = ToolResults.Text(result);
            var commands = structured.GetProperty("commands");

            Assert.Equal(commands.GetArrayLength(), structured.GetProperty("count").GetInt32());

            // A truncated answer must still be honest about every cmdlet it does list.
            foreach (var command in commands.EnumerateArray())
            {
                Assert.Contains(command.GetProperty("name").GetString()!, text, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>Truncated must mean what it says, in both directions.</summary>
    [Fact]
    public void The_truncated_flag_tracks_reality()
    {
        using (new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", "50000"))
        {
            var small = PnPPowerShellTools.SearchPnpCommands("Get-PnPWeb", 3).StructuredContent!.Value;
            Assert.False(small.GetProperty("truncated").GetBoolean(), "A result that fits was marked truncated.");
        }

        using (new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", OutputLimit.MinimumMaxChars.ToString()))
        {
            var big = PnPPowerShellTools.SearchPnpCommands("site list file user group", 100).StructuredContent!.Value;
            Assert.True(big.GetProperty("truncated").GetBoolean(), "A result that was cut was not marked truncated.");
        }
    }

    /// <summary>How far the loop actually has to shrink, printed so a regression in cost is visible.</summary>
    [Fact]
    public void Report_what_survives_each_cap()
    {
        foreach (var cap in new[] { OutputLimit.MinimumMaxChars, 5_000, 12_000, 50_000 })
        {
            using var capped = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", cap.ToString());

            var result = PnPPowerShellTools.SearchPnpCommands("site list file user group set get add remove", 100);
            var structured = result.StructuredContent!.Value;
            var text = ToolResults.Text(result);
            var json = structured.GetRawText();
            var lean = !structured.GetProperty("commands")[0].TryGetProperty("parameters", out _);

            output.WriteLine(
                $"cap {cap,6}: {structured.GetProperty("count").GetInt32(),3} hits, " +
                $"text {text.Length,6}, json {json.Length,6}, total {text.Length + json.Length,6}, lean={lean}");
        }
    }

    private static string Describe(string query) =>
        query.Length > 40 ? $"{query[..20]}...({query.Length} chars)" : query;
}
