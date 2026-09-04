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
    public void A_comment_inside_a_code_fence_does_not_close_the_section()
    {
        // "# A person signing in" inside the auth section's PowerShell block used to end the section.
        const string fenced = """
            ## Sessions

            First.

            ```powershell
            # A comment, not a heading
            ## Nor is this
            Connect-PnPOnline -Url https://contoso.sharepoint.com
            ```

            Still sessions.

            ## Read-Only Mode

            Not part of it.
            """;

        var result = PnPPowerShellTools.ExtractSections(fenced, ["Sessions"]);

        Assert.Contains("# A comment, not a heading", result);
        Assert.Contains("Connect-PnPOnline", result);
        Assert.Contains("Still sessions.", result);
        Assert.DoesNotContain("Not part of it.", result);
    }

    [Fact]
    public void Every_advertised_section_resolves_against_the_shipped_document()
    {
        // Guards the real failure mode: renaming a heading in best-practices.md silently empties a
        // section. Iterates the dictionary itself so a newly added section is covered automatically.
        foreach (var key in PnPPowerShellTools.BestPracticeSections.Keys)
        {
            var result = PnPPowerShellTools.GetPnpBestPractices(key);

            Assert.False(
                result.StartsWith("Error:", StringComparison.Ordinal),
                $"Section '{key}' did not resolve: {result}");
            Assert.True(result.Length > 100, $"Section '{key}' returned suspiciously little content.");
        }
    }

    [Fact]
    public void The_auth_section_names_the_registration_decision_and_the_default_grant()
    {
        var auth = PnPPowerShellTools.GetPnpBestPractices("auth");

        Assert.Contains("Register-PnPEntraIDAppForInteractiveLogin", auth);
        Assert.Contains("Register-PnPEntraIDApp ", auth);
        Assert.Contains("Ask which one the user wants", auth);
        Assert.Contains("No permissions specified, using default permissions", auth);
        Assert.Contains("Sites.FullControl.All", auth);
        Assert.Contains("AllSites.FullControl", auth);
        Assert.Contains("-SharePointDelegatePermissions", auth);
        Assert.Contains("AZURE_CLIENT_ID", auth);

        // Register-PnPEntraIDAppForInteractiveLogin has no -Interactive switch.
        Assert.DoesNotContain("-Interactive", auth);
    }

    [Fact]
    public void The_trust_section_says_returned_content_is_data_and_names_what_is_not_done()
    {
        var trust = PnPPowerShellTools.GetPnpBestPractices("trust");

        Assert.Contains("data, not instructions", trust);
        Assert.Contains("never run on that basis alone", trust);
        Assert.Contains("does not sanitise", trust);
        Assert.Contains("not closed", trust);
    }

    [Fact]
    public void An_unknown_section_is_rejected_with_the_valid_list()
    {
        var result = PnPPowerShellTools.GetPnpBestPractices("nonsense");

        Assert.StartsWith("Error:", result);
        Assert.Contains("sessions", result);
    }

    [Fact]
    public void Omitting_the_section_returns_the_whole_document()
    {
        var whole = PnPPowerShellTools.GetPnpBestPractices();
        var slice = PnPPowerShellTools.GetPnpBestPractices("sessions");

        Assert.True(whole.Length > slice.Length * 2, "The full document should be substantially larger than one section.");
        Assert.Contains("Read-Only Mode", whole);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_is_treated_as_no_section(string section)
    {
        Assert.Contains("Read-Only Mode", PnPPowerShellTools.GetPnpBestPractices(section));
    }

    [Fact]
    public void A_section_name_is_trimmed_before_lookup()
    {
        var result = PnPPowerShellTools.GetPnpBestPractices("  readonly  ");

        Assert.DoesNotContain("Error:", result);
    }
}

/// <summary>Guards the lists that must be updated by hand whenever a section is added or renamed.</summary>
public class BestPracticeSectionDriftTests
{
    private static string SectionParameterDescription()
    {
        var method = typeof(PnPPowerShellTools).GetMethod(nameof(PnPPowerShellTools.GetPnpBestPractices))!;
        var parameter = method.GetParameters().Single(p => p.Name == "section");

        return parameter
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
            .Cast<System.ComponentModel.DescriptionAttribute>()
            .Single()
            .Description;
    }

    [Fact]
    public void The_tool_description_advertises_every_section()
    {
        // The attribute needs a compile-time constant, so the list is hand-written and can drift from
        // the dictionary. This is what the model reads, so a stale list means an unusable section.
        var description = SectionParameterDescription();

        foreach (var key in PnPPowerShellTools.BestPracticeSections.Keys)
        {
            Assert.True(
                description.Contains(key, StringComparison.OrdinalIgnoreCase),
                $"Section '{key}' is missing from the tool's parameter description.");
        }
    }

    [Fact]
    public void The_shipped_guidance_mentions_every_section()
    {
        // best-practices.md tells the reader which sections exist; it is embedded, so it can be checked.
        var document = PnPPowerShellTools.GetPnpBestPractices();

        foreach (var key in PnPPowerShellTools.BestPracticeSections.Keys)
        {
            Assert.True(
                document.Contains($"`{key}`", StringComparison.OrdinalIgnoreCase),
                $"Section '{key}' is not listed in best-practices.md.");
        }
    }
}
