using PnPPowerShell.MCPServer.Services;
using PnPPowerShell.MCPServer.Tools;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>
/// Roadmap #19 part 1: README bodies fetched from GitHub are third-party content, so the two tools that
/// return them lead with a line marking what follows as data. Tenant output the user asked for is not
/// marked, and a test pins that so the boundary cannot quietly widen.
/// </summary>
public class DataBoundaryTests
{
    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "fixtures");

    private const string Query = "export list items csv";

    // Search and suggest share one ranking, so the search's first hit is the sample suggest will fetch.
    private static string TopHitFor(string query) =>
        ScriptSampleTools.SearchScriptSamples(query, 1).StructuredContent!.Value
            .GetProperty("samples")[0].GetProperty("name").GetString()!;

    [Fact]
    public async Task A_fetched_sample_leads_with_the_data_boundary_marker()
    {
        var sample = ScriptSampleIndex.Samples[0];
        using var clone = new LocalClone(sample.Name, "Get-PnPWeb");

        var output = await ScriptSampleTools.GetScriptSample(sample.Name);

        Assert.StartsWith(ScriptSampleTools.FetchedContentNotice, output, StringComparison.Ordinal);
        Assert.Contains("Get-PnPWeb", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_suggested_script_leads_with_the_data_boundary_marker()
    {
        using var clone = new LocalClone(TopHitFor(Query), "Get-PnPWeb");

        var output = await ScriptSampleTools.SuggestScript(Query, 1);

        Assert.StartsWith(ScriptSampleTools.FetchedContentNotice, output, StringComparison.Ordinal);
        Assert.Contains("Get-PnPWeb", output, StringComparison.Ordinal);
    }

    /// <summary>The marker leads the body and Apply keeps the head, so truncation cannot take it.</summary>
    [Theory]
    [InlineData("get")]
    [InlineData("suggest")]
    public async Task The_marker_survives_a_response_that_has_to_be_truncated(string tool)
    {
        var name = TopHitFor(Query);
        using var clone = new LocalClone(name, new string('Z', 100_000));
        using var cap = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", OutputLimit.MinimumMaxChars.ToString());

        var output = tool == "get"
            ? await ScriptSampleTools.GetScriptSample(name)
            : await ScriptSampleTools.SuggestScript(Query, 1);

        Assert.Contains(OutputLimit.TruncationMarker, output, StringComparison.Ordinal);
        Assert.StartsWith(ScriptSampleTools.FetchedContentNotice, output, StringComparison.Ordinal);
        Assert.Contains(ScriptSampleIndex.Provenance, output, StringComparison.Ordinal);
        Assert.True(output.Length <= OutputLimit.MaxChars, $"{output.Length} characters against a {OutputLimit.MaxChars} cap.");
    }

    /// <summary>Deliberately narrow: tenant output the user asked for is not third-party content.</summary>
    [PlaybackFact]
    public async Task Run_command_output_is_not_prefixed()
    {
        using var replay = new EnvVar("PNP_MCP_REPLAY_DIR", FixtureDirectory);
        await using var sessions = new PowerShellSessionManager();

        var output = await PnPPowerShellTools.RunPnpCommand(sessions, server: null!, context: null!, "Get-PnPWeb | Select-Object Title, Url");

        Assert.Contains("Url", output, StringComparison.Ordinal);
        Assert.DoesNotContain(ScriptSampleTools.FetchedContentNotice, output, StringComparison.Ordinal);
    }

    /// <summary>A PNP_SCRIPT_SAMPLES_PATH clone holding one README, so the fetch is offline and its size is chosen.</summary>
    private sealed class LocalClone : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("pnp-boundary");
        private readonly EnvVar _path;

        public LocalClone(string sampleName, string script)
        {
            var folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "scripts", sampleName));
            File.WriteAllText(Path.Combine(folder.FullName, "README.md"), $"# Sample\n\n```powershell\n{script}\n```\n");
            _path = new EnvVar("PNP_SCRIPT_SAMPLES_PATH", _root.FullName);
        }

        public void Dispose()
        {
            _path.Dispose();
            _root.Delete(recursive: true);
        }
    }
}
