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
        Assert.Contains(ScriptSampleIndex.Provenance, ScriptSampleTools.SearchScriptSamples("site", 1), StringComparison.Ordinal);
    }

    [Fact]
    public void Searching_samples_needs_no_network_and_no_extension()
    {
        var results = ScriptSampleTools.SearchScriptSamples("document set", 5);

        Assert.DoesNotContain("No script sample source was found", results, StringComparison.Ordinal);
        Assert.Contains("**Name**:", results, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unmatched_sample_search_still_points_somewhere()
    {
        var results = ScriptSampleTools.SearchScriptSamples("zzzzznotathing", 5);

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

    [Fact]
    public void Searches_cmdlet_names_offline()
    {
        var matches = CommandIndex.Search(["tenant", "site"], 10);

        Assert.Contains("Get-PnPTenantSite", matches);
        Assert.True(matches.Count <= 10);
    }

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

    /// <summary>The fallback must keep its cmdlets when a large session error is truncated away.</summary>
    [Fact]
    public async Task The_vendored_fallback_survives_a_session_error_too_large_to_show()
    {
        var directory = Directory.CreateTempSubdirectory("pnp-fallback");

        try
        {
            var failure = "Error: the command failed\n\nOutput before the failure:\n" + new string('x', 200_000);
            var key = SessionTranscript.Key(string.Empty, "search-commands\ntenant site\n5");
            File.WriteAllText(Path.Combine(directory.FullName, key + ".transcript"), $"# key: {key}\n\n--- output ---\n{failure}");

            using var replay = new EnvVar("PNP_MCP_REPLAY_DIR", directory.FullName);
            await using var sessions = new PowerShellSessionManager();

            var output = await PnPPowerShellTools.SearchPnpCommands(sessions, "tenant site", 5);

            Assert.True(output.Length <= OutputLimit.MaxChars, $"{output.Length} characters against a {OutputLimit.MaxChars} cap.");
            Assert.Contains("Get-PnPTenantSite", output, StringComparison.Ordinal);
            Assert.Contains(CommandIndex.Provenance, output, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Provenance is a footer, so truncation takes it first — exactly when it is most wanted.</summary>
    [Theory]
    [InlineData("search")]
    [InlineData("suggest")]
    public async Task Provenance_survives_a_response_that_has_to_be_truncated(string tool)
    {
        using var cap = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", OutputLimit.MinimumMaxChars.ToString());

        var output = tool == "search"
            ? ScriptSampleTools.SearchScriptSamples("site list user teams permission export", 50)
            : await ScriptSampleTools.SuggestScript("export every list item to a csv file", 5);

        Assert.Contains(OutputLimit.TruncationMarker, output, StringComparison.Ordinal);
        Assert.Contains(ScriptSampleIndex.Provenance, output, StringComparison.Ordinal);
        Assert.True(output.Length <= OutputLimit.MaxChars, $"{output.Length} characters against a {OutputLimit.MaxChars} cap.");
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
