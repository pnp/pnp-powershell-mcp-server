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

        var kept = ScriptSampleIndex.NormalizeForTest(root);

        Assert.Equal(["spo-good-sample"], kept.Select(s => s.Name));
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
