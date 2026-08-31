using PnPPowerShell.MCPServer.Services;
using PnPPowerShell.MCPServer.Tools;
using Xunit.Abstractions;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>
/// Shrinking to fit is only safe if the answer says it shrank. A count that silently describes the page
/// rather than the world is a false statement, not a truncated one.
/// </summary>
public class TruncationHonestyTests(ITestOutputHelper output)
{
    /// <summary>"N active session(s)" is a claim about the machine, not about how many fitted.</summary>
    [Fact]
    public async Task A_truncated_session_list_does_not_understate_how_many_exist()
    {
        using var capped = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", OutputLimit.MinimumMaxChars.ToString());
        await using var sessions = new PowerShellSessionManager();

        // Enough sessions, with long enough ids, that the list cannot fit a small cap.
        for (var i = 0; i < 40; i++)
        {
            sessions.Get($"session-{i:D2}-{new string('x', 60)}");
        }

        var result = PnPPowerShellTools.ListSessions(sessions);
        var text = ToolResults.Text(result);
        var structured = result.StructuredContent!.Value;

        var shown = structured.GetProperty("count").GetInt32();
        var truncated = structured.GetProperty("truncated").GetBoolean();

        output.WriteLine($"created 40, shown {shown}, truncated {truncated}");
        output.WriteLine(text.Split('\n')[0]);

        Assert.True(truncated, "40 sessions at the minimum cap were not reported as truncated.");

        // The prose must not present a partial page as the total.
        Assert.False(
            text.Contains($"**{shown}** active session(s)", StringComparison.Ordinal) &&
            !text.Contains("more", StringComparison.OrdinalIgnoreCase),
            $"The page shows {shown} of 40 sessions but states it as the total with no note.");
    }

    /// <summary>A declared output schema with no payload and no explanation leaves a client guessing.</summary>
    [Fact]
    public async Task Dropping_the_structured_half_is_visible_in_the_text()
    {
        var directory = Directory.CreateTempSubdirectory("pnp-drop");

        try
        {
            // A *valid* status whose text half consumes the budget, so there is real data to omit. A
            // session error is a different case: it has nothing to serialize, so no payload is withheld
            // and no notice is owed.
            var payload = $$"""{"connected":true,"url":"https://contoso.sharepoint.com/sites/{{new string('u', 120_000)}}","connectionType":"O365"}""";
            var key = SessionTranscript.Key(string.Empty, "connection-status");
            File.WriteAllText(Path.Combine(directory.FullName, key + ".transcript"), $"# key: {key}\n\n--- output ---\n{payload}");

            using var replay = new EnvVar("PNP_MCP_REPLAY_DIR", directory.FullName);
            await using var sessions = new PowerShellSessionManager();

            var result = await PnPPowerShellTools.GetPnpConnectionStatus(sessions);
            var text = ToolResults.Text(result);

            output.WriteLine($"structured={(result.StructuredContent is null ? "none" : "present")} text={text.Length}");

            // The tool declares an output schema. If no payload is sent, the prose has to account for it,
            // or a client that trusted the schema is left unable to tell "absent" from "broken".
            // Asserted on the specific notice, not on the word "truncated" -- OutputLimit's own marker
            // contains that word, so the looser check passed for the wrong reason.
            if (result.StructuredContent is null)
            {
                Assert.Contains("structured output omitted", text, StringComparison.Ordinal);
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
