using PnPPowerShell.MCPServer.Tools;

namespace PnPPowerShell.MCPServer.Tests;

public class BestPracticeSectionTests
{
    private const string Document = """
        # Title

        Intro prose.

        ## Sessions

        Session guidance.

        ### A subheading

        Still session guidance.

        ## Read-Only Mode

        Read-only guidance.

        ## Destructive Commands

        Destructive guidance.
        """;

    [Fact]
    public void A_named_section_is_returned_without_its_neighbours()
    {
        var result = PnPPowerShellTools.ExtractSections(Document, ["Sessions"]);

        Assert.Contains("Session guidance.", result);
        Assert.DoesNotContain("Read-only guidance.", result);
        Assert.DoesNotContain("Destructive guidance.", result);
        Assert.DoesNotContain("Intro prose.", result);
    }

    [Fact]
    public void A_subheading_does_not_end_the_section()
    {
        // Only level-2 headings delimit a section, so "### A subheading" must not truncate it.
        var result = PnPPowerShellTools.ExtractSections(Document, ["Sessions"]);

        Assert.Contains("### A subheading", result);
        Assert.Contains("Still session guidance.", result);
    }

    [Fact]
    public void Several_sections_come_back_in_document_order()
    {
        var result = PnPPowerShellTools.ExtractSections(Document, ["Destructive Commands", "Sessions"]);

        Assert.True(
            result.IndexOf("Session guidance.", StringComparison.Ordinal)
                < result.IndexOf("Destructive guidance.", StringComparison.Ordinal),
            "Sections should follow the document, not the order they were requested in.");
    }

    [Fact]
    public void Heading_matching_is_case_insensitive()
    {
        Assert.Contains("Session guidance.", PnPPowerShellTools.ExtractSections(Document, ["sessions"]));
    }

    [Fact]
    public void An_unknown_heading_yields_nothing()
    {
        Assert.True(string.IsNullOrWhiteSpace(PnPPowerShellTools.ExtractSections(Document, ["Nope"])));
    }

    [Fact]
    public void A_top_level_heading_closes_the_current_section()
    {
        const string twoDocs = """
            ## Sessions

            First.

            # Another Document

            Not part of it.
            """;

        var result = PnPPowerShellTools.ExtractSections(twoDocs, ["Sessions"]);

        Assert.Contains("First.", result);
        Assert.DoesNotContain("Not part of it.", result);
    }

    [Fact]
    public async Task Every_advertised_section_resolves_against_the_shipped_document()
    {
        // Guards the real failure mode: renaming a heading in best-practices.md silently empties a section.
        foreach (var key in new[]
                 {
                     "workflow", "docs", "sessions", "config", "readonly",
                     "destructive", "auth", "execution", "patterns",
                 })
        {
            var result = await PnPPowerShellTools.GetPnpBestPractices(key);

            Assert.False(
                result.StartsWith("Error:", StringComparison.Ordinal),
                $"Section '{key}' did not resolve: {result}");
            Assert.True(result.Length > 100, $"Section '{key}' returned suspiciously little content.");
        }
    }

    [Fact]
    public async Task An_unknown_section_is_rejected_with_the_valid_list()
    {
        var result = await PnPPowerShellTools.GetPnpBestPractices("nonsense");

        Assert.StartsWith("Error:", result);
        Assert.Contains("sessions", result);
    }

    [Fact]
    public async Task Omitting_the_section_returns_the_whole_document()
    {
        var whole = await PnPPowerShellTools.GetPnpBestPractices();
        var slice = await PnPPowerShellTools.GetPnpBestPractices("sessions");

        Assert.True(whole.Length > slice.Length * 2, "The full document should be substantially larger than one section.");
        Assert.Contains("Read-Only Mode", whole);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_is_treated_as_no_section(string section)
    {
        Assert.Contains("Read-Only Mode", await PnPPowerShellTools.GetPnpBestPractices(section));
    }

    [Fact]
    public async Task A_section_name_is_trimmed_before_lookup()
    {
        var result = await PnPPowerShellTools.GetPnpBestPractices("  readonly  ");

        Assert.DoesNotContain("Error:", result);
    }
}
