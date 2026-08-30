using PnPPowerShell.MCPServer.Services;
using PnPPowerShell.MCPServer.Tools;
using Xunit.Abstractions;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>Attacks the three tools converted last: sample search, diagnosis and setup.</summary>
public class ConversionAdversarialTests(ITestOutputHelper output)
{
    private static void WriteFixture(DirectoryInfo directory, string operation, string content)
    {
        var key = SessionTranscript.Key(string.Empty, operation);
        File.WriteAllText(Path.Combine(directory.FullName, key + ".transcript"), $"# key: {key}\n\n--- output ---\n{content}");
    }

    /// <summary>
    /// The query is clamped into the record and clamped again when the no-match path renders it. That
    /// double application is harmless only because Echo is idempotent — a clamped value is already at
    /// the limit, so re-clamping returns it unchanged. Asserted here because the safety is a property of
    /// Echo rather than of the call sites, and a future change to Echo would break the callers silently.
    /// </summary>
    [Theory]
    [InlineData(400)]
    [InlineData(121)]
    [InlineData(120)]
    [InlineData(5)]
    public void Echo_is_idempotent_so_clamping_a_clamped_query_is_safe(int length)
    {
        // Varied characters, so a re-clamp that shifted the cut point would be visible.
        var query = string.Concat(Enumerable.Range(0, length).Select(i => (char)('a' + (i % 26))));

        var once = OutputLimit.Echo(query);

        Assert.Equal(once, OutputLimit.Echo(once));
        Assert.Contains(once, ToolResults.Text(ScriptSampleTools.SearchScriptSamples(query, 5)), StringComparison.Ordinal);
    }

    /// <summary>Rendering looks each page entry back up by name, which duplicates would make ambiguous.</summary>
    [Fact]
    public void Sample_names_are_unique() =>
        Assert.Empty(ScriptSampleIndex.Samples
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key));

    /// <summary>Sample search must stay inside the cap at every budget, like the cmdlet search does.</summary>
    [Theory]
    [InlineData(2_000)]
    [InlineData(5_000)]
    [InlineData(50_000)]
    public void Sample_search_keeps_both_halves_inside_the_cap(int cap)
    {
        using var capped = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", cap.ToString());

        var result = ScriptSampleTools.SearchScriptSamples("site list user teams permission export", 50);
        var text = ToolResults.Text(result);
        var json = result.StructuredContent is { } s ? s.GetRawText() : string.Empty;

        output.WriteLine($"cap {cap}: text {text.Length} + json {json.Length} = {text.Length + json.Length}");

        Assert.True(text.Length + json.Length <= cap, $"text {text.Length} + json {json.Length} exceeds {cap}.");
    }

    /// <summary>A no-match search still has to answer, and still name where the catalogue came from.</summary>
    [Fact]
    public void An_unmatched_sample_search_answers_with_an_empty_typed_result()
    {
        var result = ScriptSampleTools.SearchScriptSamples("zzzzznotathing", 5);
        var structured = result.StructuredContent!.Value;

        Assert.Equal(0, structured.GetProperty("count").GetInt32());
        Assert.Equal(0, structured.GetProperty("matched").GetInt32());
        Assert.False(structured.GetProperty("truncated").GetBoolean());
        Assert.Contains(ScriptSampleIndex.Provenance, ToolResults.Text(result), StringComparison.Ordinal);
    }

    /// <summary>
    /// Diagnosis renders a long fixed report, so at a low cap the text can consume the whole budget and
    /// the structured half is dropped. That is allowed, but it must be said rather than silent.
    /// </summary>
    [Fact]
    public async Task Diagnosis_at_a_low_cap_either_carries_its_payload_or_says_it_did_not()
    {
        var directory = Directory.CreateTempSubdirectory("pnp-diag-cap");

        try
        {
            WriteFixture(directory, "environment-probe", """{"pwshVersion":"7.4.6","moduleVersion":"3.4.1"}""");

            using var replay = new EnvVar("PNP_MCP_REPLAY_DIR", directory.FullName);
            using var capped = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", OutputLimit.MinimumMaxChars.ToString());
            await using var sessions = new PowerShellSessionManager();

            var result = await PnPPowerShellTools.DiagnosePnpConnection(sessions, null, "https://contoso.sharepoint.com/sites/x");
            var text = ToolResults.Text(result);
            var json = result.StructuredContent is { } s ? s.GetRawText() : string.Empty;

            output.WriteLine($"text {text.Length} + json {json.Length}, structured={(result.StructuredContent is null ? "dropped" : "present")}");

            Assert.True(text.Length + json.Length <= OutputLimit.MaxChars);

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

    /// <summary>Diagnosis must report the machine it probed, not a default-shaped guess.</summary>
    [Theory]
    [InlineData("""{"pwshVersion":"7.4.6","moduleVersion":"3.4.1"}""", true, true)]
    [InlineData("""{"pwshVersion":"7.4.6","moduleVersion":null}""", true, false)]
    [InlineData("""{"pwshVersion":null,"moduleVersion":null}""", false, false)]
    public async Task Diagnosis_reports_the_environment_it_probed(string probe, bool pwsh, bool module)
    {
        var directory = Directory.CreateTempSubdirectory("pnp-diag-state");

        try
        {
            WriteFixture(directory, "environment-probe", probe);

            using var replay = new EnvVar("PNP_MCP_REPLAY_DIR", directory.FullName);
            await using var sessions = new PowerShellSessionManager();

            var structured = (await PnPPowerShellTools.DiagnosePnpConnection(sessions)).StructuredContent!.Value;

            Assert.Equal(pwsh, structured.GetProperty("pwshAvailable").GetBoolean());
            Assert.Equal(module, structured.GetProperty("moduleInstalled").GetBoolean());

            // Ready is derived, so it cannot claim more than the checks support.
            Assert.False(structured.GetProperty("ready").GetBoolean(), "Nothing is connected in playback, so ready must be false.");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>The version is read back from the install's own success line, so its shape matters.</summary>
    [Theory]
    [InlineData("Installed PnP.PowerShell 3.4.1. Next: call pnp_diagnose_connection.", "3.4.1", true)]
    [InlineData("Installed PnP.PowerShell 3.5.0-nightly. Next: call pnp_diagnose_connection.", "3.5.0-nightly", true)]
    [InlineData("Error: the install command reported success but PnP.PowerShell is still not visible.", null, false)]
    [InlineData("Error: No match was found for the specified search criteria.", null, false)]
    public async Task Setup_reports_installed_only_when_the_script_said_so(string scriptOutput, string? version, bool installed)
    {
        var directory = Directory.CreateTempSubdirectory("pnp-setup-parse");

        try
        {
            WriteFixture(directory, "environment-probe", """{"pwshVersion":"7.4.6","moduleVersion":null}""");
            WriteFixture(directory, "setup-environment", scriptOutput);

            using var allow = new EnvVar("PNP_MCP_ALLOW_SETUP", "true");
            using var replay = new EnvVar("PNP_MCP_REPLAY_DIR", directory.FullName);
            await using var sessions = new PowerShellSessionManager();

            var structured = (await PnPPowerShellTools.SetupEnvironment(sessions)).StructuredContent!.Value;

            Assert.True(structured.GetProperty("allowed").GetBoolean());
            Assert.Equal(installed, structured.GetProperty("installed").GetBoolean());
            Assert.Equal(version, structured.TryGetProperty("moduleVersion", out var v) ? v.GetString() : null);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>The declined path must report that nothing ran, and still hand over the command.</summary>
    [Fact]
    public async Task Setup_declined_reports_not_allowed_and_not_installed()
    {
        using var allow = new EnvVar("PNP_MCP_ALLOW_SETUP", null);
        await using var sessions = new PowerShellSessionManager();

        var structured = (await PnPPowerShellTools.SetupEnvironment(sessions)).StructuredContent!.Value;

        Assert.False(structured.GetProperty("allowed").GetBoolean());
        Assert.False(structured.GetProperty("installed").GetBoolean());
        Assert.Equal(PnPPowerShellTools.InstallModuleCommand(false), structured.GetProperty("command").GetString());
    }
}
