using System.Reflection;
using PnPPowerShell.MCPServer.Models;
using PnPPowerShell.MCPServer.Services;
using PnPPowerShell.MCPServer.Tools;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>The vendored indexes are what make three tools work without a VS Code extension installed.</summary>
public class VendoredIndexTests
{
    [Fact]
    public void The_sample_index_is_present_and_populated()
    {
        Assert.True(ScriptSampleIndex.Samples.Count > 250, $"Only {ScriptSampleIndex.Samples.Count} samples loaded.");
        Assert.All(ScriptSampleIndex.Samples, s =>
        {
            Assert.NotEmpty(s.Name);
            Assert.NotEmpty(s.Title);
            Assert.StartsWith("https://pnp.github.io/script-samples/", s.Url, StringComparison.Ordinal);
            Assert.StartsWith("https://raw.githubusercontent.com/pnp/script-samples/", s.RawUrl, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void The_sample_index_says_where_it_came_from()
    {
        // A stale index has to be visible rather than silent, so provenance is printed with every answer.
        Assert.Contains("samples", ScriptSampleIndex.Provenance, StringComparison.Ordinal);
        Assert.Contains(ScriptSampleIndex.Provenance, ToolResults.Text(ScriptSampleTools.SearchScriptSamples("site", 1)), StringComparison.Ordinal);
    }

    [Fact]
    public void Searching_samples_needs_no_network_and_no_extension()
    {
        var results = ToolResults.Text(ScriptSampleTools.SearchScriptSamples("document set", 5));

        Assert.DoesNotContain("No script sample source was found", results, StringComparison.Ordinal);
        Assert.Contains("**Name**:", results, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unmatched_sample_search_still_points_somewhere()
    {
        var results = ToolResults.Text(ScriptSampleTools.SearchScriptSamples("zzzzznotathing", 5));

        Assert.Contains("https://pnp.github.io/script-samples/", results, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cmdlet_index_is_present_and_populated() =>
        Assert.True(CommandIndex.Commands.Count > 800, $"Only {CommandIndex.Commands.Count} cmdlets loaded.");

    [Theory]
    [InlineData("Get-PnPWeb", "Get-PnPWeb")]
    [InlineData("get-pnpweb", "Get-PnPWeb")]
    [InlineData("  Connect-PnPOnline  ", "Connect-PnPOnline")]
    public void Resolves_a_cmdlet_to_its_documented_casing(string input, string expected) =>
        Assert.Equal(expected, CommandIndex.Resolve(input));

    [Theory]
    [InlineData("Get-PnPNotACmdlet")]
    [InlineData("")]
    [InlineData(null)]
    public void Resolves_nothing_for_an_unknown_name(string? input)
    {
        Assert.Null(CommandIndex.Resolve(input));
        Assert.Null(CommandIndex.MarkdownUrl(input));
        Assert.Null(CommandIndex.DocsUrl(input));
    }

    [Fact]
    public void Offers_the_markdown_source_alongside_the_html_page()
    {
        Assert.Equal(
            "https://raw.githubusercontent.com/pnp/powershell/dev/documentation/Get-PnPWeb.md",
            CommandIndex.MarkdownUrl("get-pnpweb"));

        Assert.Equal("https://pnp.github.io/powershell/cmdlets/Get-PnPWeb.html", CommandIndex.DocsUrl("get-pnpweb"));
    }

    // Removed with CommandIndex.Search: keyword scoring over bare names existed only as the fallback for
    // pnp_search_commands, which CommandCorpus now answers offline. CommandCorpusTests covers searching.

    /// <summary>A sample name reaches both a file path and a URL, and two of the three sources are not ours.</summary>
    [Theory]
    [InlineData("../../../../etc/passwd")]
    [InlineData("..\\..\\Windows\\System32\\config")]
    [InlineData("spo/../../secrets")]
    [InlineData("..")]
    [InlineData("a:b")]
    [InlineData("")]
    public void A_sample_whose_name_is_not_a_plain_folder_segment_is_dropped(string name)
    {
        var root = new SamplesRoot
        {
            UrlTemplate = "https://pnp.github.io/script-samples/{name}/README.html",
            RawUrlTemplate = "https://raw.githubusercontent.com/pnp/script-samples/main/scripts/{name}/README.md",
            Samples = [new ScriptSample { Name = name, Title = "tampered" }, new ScriptSample { Name = "spo-good-sample", Title = "fine" }],
        };

        ScriptSampleIndex.Normalize(root);

        Assert.Equal(["spo-good-sample"], root.Samples.Select(s => s.Name));
    }

    /// <summary>The docs error path returned raw, and a session error carries unbounded prior output.</summary>
    [Fact]
    public async Task Command_docs_stays_within_the_cap_when_the_session_fails_with_a_large_error()
    {
        var directory = Directory.CreateTempSubdirectory("pnp-docs-cap");

        try
        {
            // A command that printed a lot and then failed: PowerShellSession returns both, uncapped.
            var failure = "Error: the command failed\n\nOutput before the failure:\n" + new string('x', 200_000);
            var key = SessionTranscript.Key(string.Empty, "command-docs\nGet-PnPWeb");
            File.WriteAllText(Path.Combine(directory.FullName, key + ".transcript"), $"# key: {key}\n\n--- output ---\n{failure}");

            using var replay = new EnvVar("PNP_MCP_REPLAY_DIR", directory.FullName);
            await using var sessions = new PowerShellSessionManager();

            var output = await PnPPowerShellTools.GetPnpCommandDocs(sessions, "Get-PnPWeb");

            Assert.True(output.Length <= OutputLimit.MaxChars, $"{output.Length} characters against a {OutputLimit.MaxChars} cap.");
            Assert.Contains("Get-PnPWeb.md", output, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Search needs no session at all now, so a broken environment cannot degrade it.</summary>
    // This replaces a test of the old pwsh fallback: there is no live search left to fall back from.
    [Fact]
    public void Searching_cmdlets_needs_no_session()
    {
        using var replay = new EnvVar("PNP_MCP_REPLAY_DIR", Path.Combine(Path.GetTempPath(), "pnp-no-such-replay-dir"));

        var output = ToolResults.Text(PnPPowerShellTools.SearchPnpCommands("Get-PnPTenantSite", 5));

        Assert.True(output.Length <= OutputLimit.MaxChars, $"{output.Length} characters against a {OutputLimit.MaxChars} cap.");
        Assert.Contains("Get-PnPTenantSite", output, StringComparison.Ordinal);
        Assert.Contains(CommandCorpus.Provenance, output, StringComparison.Ordinal);
    }

    /// <summary>Provenance is a footer, so truncation takes it first — exactly when it is most wanted.</summary>
    [Fact]
    public async Task Provenance_survives_a_response_that_has_to_be_truncated()
    {
        using var cap = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", OutputLimit.MinimumMaxChars.ToString());

        var output = await ScriptSampleTools.SuggestScript("export every list item to a csv file", 5);

        Assert.Contains(OutputLimit.TruncationMarker, output, StringComparison.Ordinal);
        Assert.Contains(ScriptSampleIndex.Provenance, output, StringComparison.Ordinal);
        Assert.True(output.Length <= OutputLimit.MaxChars, $"{output.Length} characters against a {OutputLimit.MaxChars} cap.");
    }

    /// <summary>
    /// Sample search no longer truncates: it returns fewer whole samples instead.
    ///
    /// The theory case that used to live above asserted the mid-content truncation marker, which is now
    /// the wrong contract — cutting a sample list mid-entry was what structured output replaced. What has
    /// to stay true is that the answer fits, says how many it dropped, and still names its provenance.
    /// </summary>
    [Fact]
    public void A_sample_search_too_large_to_fit_drops_whole_samples_rather_than_characters()
    {
        using var cap = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", OutputLimit.MinimumMaxChars.ToString());

        var result = ScriptSampleTools.SearchScriptSamples("site list user teams permission export", 50);
        var text = ToolResults.Text(result);
        var structured = result.StructuredContent!.Value;

        Assert.DoesNotContain(OutputLimit.TruncationMarker, text, StringComparison.Ordinal);
        Assert.Contains(ScriptSampleIndex.Provenance, text, StringComparison.Ordinal);
        Assert.True(text.Length <= OutputLimit.MaxChars, $"{text.Length} characters against a {OutputLimit.MaxChars} cap.");

        // Fewer listed than matched, and the prose says so rather than presenting the page as the whole.
        Assert.True(structured.GetProperty("truncated").GetBoolean());
        Assert.True(structured.GetProperty("count").GetInt32() < structured.GetProperty("matched").GetInt32());
        Assert.Contains("showing the first", text, StringComparison.Ordinal);
    }

    /// <summary>The PNP_SCRIPT_SAMPLES_PATH override, the only source with no coverage.</summary>
    // By reflection: ScriptSampleIndex.Samples is a Lazy already resolved.
    [Fact]
    public void A_local_clone_override_is_read_from_its_per_sample_manifests()
    {
        var clone = Directory.CreateTempSubdirectory("pnp-clone");

        try
        {
            var assets = Directory.CreateDirectory(Path.Combine(clone.FullName, "scripts", "spo-demo-sample", "assets"));
            File.WriteAllText(Path.Combine(assets.FullName, "sample.json"), """
                [{"name":"ignored","title":"Demo sample","shortDescription":"A local one","url":"https://example.invalid/demo","tags":["Get-PnPWeb","modern"]}]
                """);

            // A manifest folder with no assets/sample.json must be skipped rather than fault the read.
            Directory.CreateDirectory(Path.Combine(clone.FullName, "scripts", "spo-no-manifest"));

            using var path = new EnvVar("PNP_SCRIPT_SAMPLES_PATH", clone.FullName);

            var read = typeof(ScriptSampleIndex).GetMethod("ReadLocalClone", BindingFlags.NonPublic | BindingFlags.Static)!;
            var samples = (List<ScriptSample>)read.Invoke(null, null)!;

            var sample = Assert.Single(samples);
            Assert.Equal("spo-demo-sample", sample.Name);
            Assert.Equal("Demo sample", sample.Title);
            Assert.Equal("A local one", sample.Description);
            Assert.Contains("Get-PnPWeb", sample.Tags);
            Assert.Equal("https://raw.githubusercontent.com/pnp/script-samples/main/scripts/spo-demo-sample/README.md", sample.RawUrl);
        }
        finally
        {
            clone.Delete(recursive: true);
        }
    }

    [Fact]
    public void Every_vendored_sample_name_is_a_plain_folder_segment()
    {
        Assert.All(ScriptSampleIndex.Samples, s =>
        {
            Assert.Equal(s.Name, Path.GetFileName(s.Name));
            Assert.DoesNotContain("..", s.Name, StringComparison.Ordinal);
        });
    }
}
