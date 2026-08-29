using PnPPowerShell.MCPServer.Models;
using PnPPowerShell.MCPServer.Services;
using PnPPowerShell.MCPServer.Tools;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>The corpus behind pnp_search_commands: it must be complete, and it must rank sensibly.</summary>
public class CommandCorpusTests
{
    [Fact]
    public void The_corpus_is_present_and_populated()
    {
        Assert.True(CommandCorpus.Commands.Count > 800, $"Only {CommandCorpus.Commands.Count} cmdlets in the corpus.");
        Assert.False(string.IsNullOrWhiteSpace(CommandCorpus.ModuleVersion));
    }

    /// <summary>Every field search depends on has to survive generation, not just the names.</summary>
    [Fact]
    public void Every_cmdlet_carries_what_search_scores()
    {
        var commands = CommandCorpus.Commands;

        Assert.All(commands, c =>
        {
            Assert.NotEmpty(c.Name);
            Assert.NotEmpty(c.Verb);
            Assert.NotEmpty(c.Noun);
        });

        // A missing synopsis is the generator silently losing help, which would gut relevance.
        var withSynopsis = commands.Count(c => !string.IsNullOrWhiteSpace(c.Synopsis));
        Assert.True(withSynopsis > commands.Count * 0.95, $"Only {withSynopsis} of {commands.Count} cmdlets have a synopsis.");

        var withExamples = commands.Count(c => c.Examples is { Count: > 0 });
        Assert.True(withExamples > commands.Count * 0.9, $"Only {withExamples} of {commands.Count} cmdlets have an example.");

        var withParameters = commands.Count(c => c.Parameters.Count > 0);
        Assert.True(withParameters > commands.Count * 0.95, $"Only {withParameters} of {commands.Count} cmdlets have parameters.");
    }

    /// <summary>PnP writes its synopsis as a permissions block plus a sentence. Indexing the block would skew every score.</summary>
    [Fact]
    public void The_permissions_preamble_is_split_out_of_the_synopsis()
    {
        var site = CommandCorpus.Lookup("Get-PnPTenantSite");

        Assert.NotNull(site);
        Assert.Equal("Retrieves site collection information", site.Synopsis);
        Assert.NotNull(site.Permissions);
        Assert.Contains(site.Permissions, p => p.Contains("SharePoint", StringComparison.Ordinal));

        Assert.All(CommandCorpus.Commands, c =>
            Assert.DoesNotContain("Required Permissions", c.Synopsis, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Parameter sets index into the parameter list, so an off-by-one would silently mis-name parameters.</summary>
    [Fact]
    public void Parameter_set_members_resolve_to_real_parameters()
    {
        Assert.All(CommandCorpus.Commands, c =>
        {
            foreach (var set in c.ParameterSets ?? [])
            {
                Assert.All(set.Members, i => Assert.InRange(i, 0, c.Parameters.Count - 1));
                Assert.All(set.Required, i => Assert.InRange(i, 0, c.Parameters.Count - 1));
            }
        });

        var site = CommandCorpus.Lookup("Get-PnPTenantSite");
        Assert.NotNull(site);

        var byUrl = site.ParameterSets?.FirstOrDefault(s => s.Name == "By URL");
        Assert.NotNull(byUrl);
        Assert.Equal("Identity", site.Parameters[byUrl.Required.Single()].Name);
    }

    /// <summary>Plain-language questions the old -like scorer over Name/Verb/Noun could not answer at all.</summary>
    [Theory]
    // Only reachable because the description is indexed: this cmdlet's whole synopsis is "Add a field",
    // while its description says "Adds a field (a column) to a list".
    [InlineData("add a column to a list", "Add-PnPField")]
    [InlineData("create a teams channel", "Add-PnPTeamsChannel")]
    [InlineData("recycle bin", "Get-PnPRecycleBinItem")]
    [InlineData("apply a retention label to a file", "Set-PnPFileRetentionLabel")]
    [InlineData("who are the site collection administrators", "Get-PnPSiteCollectionAdmin")]
    public void Natural_language_finds_the_right_cmdlet(string query, string expected)
    {
        var names = CommandCorpus.Search(query, 10).Select(c => c.Name).ToList();

        Assert.Contains(expected, names);
    }

    /// <summary>
    /// What this index does NOT do, asserted so it is a known limit rather than a surprise.
    ///
    /// BM25 can only match words the corpus contains. Where a cmdlet's own help never uses the
    /// administrator's vocabulary, no local scoring reaches it: Get-PnPTenantSite is documented as
    /// "Retrieves site collection information" and never says "owner", so the roadmap's own motivating
    /// query cannot rank it. Fixing that means richer cmdlet help upstream, not a better scorer here.
    /// </summary>
    [Theory]
    [InlineData("find sites with no owner", "Get-PnPTenantSite")]
    [InlineData("share a file with someone outside the company", "Add-PnPFileSharingInvite")]
    public void A_query_whose_words_are_absent_from_the_help_is_not_found(string query, string expected)
    {
        var names = CommandCorpus.Search(query, 10).Select(c => c.Name).ToList();

        // Change this to the positive assertion the day the upstream help gains those words.
        Assert.DoesNotContain(expected, names);

        // It still has to answer with something in the right area rather than nothing.
        Assert.NotEmpty(names);
    }

    /// <summary>Tokenizing camel case is what lets a plain word match a cmdlet name.</summary>
    [Theory]
    [InlineData("Get-PnPTenantSite", new[] { "get", "pnp", "tenant", "site" })]
    [InlineData("Add-PnPEntraIDGroupMember", new[] { "add", "pnp", "entra", "id", "group", "member" })]
    public void Camel_case_names_split_into_words(string name, string[] expected) =>
        Assert.Equal(expected, Bm25Tokenizer.Tokenize(name));

    /// <summary>An exact cmdlet name must rank first; it is the most common query of all.</summary>
    [Theory]
    [InlineData("Get-PnPWeb")]
    [InlineData("Connect-PnPOnline")]
    [InlineData("Add-PnPListItem")]
    public void An_exact_name_ranks_first(string name) =>
        Assert.Equal(name, CommandCorpus.Search(name, 5).First().Name);

    /// <summary>Aliases stay out of results but still resolve, so a superseded name is answered rather than missed.</summary>
    [Fact]
    public void Superseded_aliases_resolve_without_polluting_results()
    {
        Assert.DoesNotContain(CommandCorpus.Commands, c => c.Name.Contains("AzureAD", StringComparison.Ordinal));

        Assert.Equal("Add-PnPEntraIDGroupMember", CommandCorpus.AliasTarget("Add-PnPAzureADGroupMember"));
        Assert.Equal("Add-PnPEntraIDGroupMember", CommandCorpus.Lookup("Add-PnPAzureADGroupMember")?.Name);
        Assert.Null(CommandCorpus.AliasTarget("Get-PnPWeb"));
    }

    [Fact]
    public void Searching_an_alias_still_answers()
    {
        var result = PnPPowerShellTools.SearchPnpCommands("Add-PnPAzureADGroupMember", 5);
        var text = ToolResults.Text(result);

        Assert.Contains("Add-PnPEntraIDGroupMember", text, StringComparison.Ordinal);
    }

    /// <summary>#12: a client that reads schemas gets typed data, not prose it has to parse.</summary>
    [Fact]
    public void The_search_tool_returns_structured_content()
    {
        var result = PnPPowerShellTools.SearchPnpCommands("tenant site", 3);

        Assert.NotNull(result.StructuredContent);

        var structured = result.StructuredContent.Value;
        Assert.Equal("tenant site", structured.GetProperty("query").GetString());
        Assert.True(structured.GetProperty("count").GetInt32() > 0);

        var first = structured.GetProperty("commands")[0];
        Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("name").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("synopsis").GetString()));
        Assert.True(first.GetProperty("parameters").GetArrayLength() > 0);

        // The prose half must still carry everything, for clients that ignore output schemas.
        var text = ToolResults.Text(result);
        Assert.Contains(first.GetProperty("name").GetString()!, text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_query_is_refused_rather_than_answered()
    {
        var result = PnPPowerShellTools.SearchPnpCommands("   ", 5);

        Assert.True(result.IsError);
        Assert.Contains("keyword", ToolResults.Text(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unmatched_query_points_somewhere_useful()
    {
        var text = ToolResults.Text(PnPPowerShellTools.SearchPnpCommands("zzzzznotathing", 5));

        Assert.Contains("No cmdlet matched", text, StringComparison.Ordinal);
        Assert.Contains("pnp.github.io", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(500, 100)]
    public void The_limit_is_clamped(int requested, int maximum) =>
        Assert.InRange(PnPPowerShellTools.SearchPnpCommands("site", requested).StructuredContent!.Value
            .GetProperty("commands").GetArrayLength(), 1, maximum);

    /// <summary>A stale index must be visible rather than silent, so the module version is always stated.</summary>
    [Fact]
    public void Answers_say_which_module_version_they_came_from()
    {
        var text = ToolResults.Text(PnPPowerShellTools.SearchPnpCommands("site", 3));

        Assert.Contains(CommandCorpus.ModuleVersion!, text, StringComparison.Ordinal);
    }

    /// <summary>#12: the schema has to reach the client, not just the value. A string-returning tool advertises a useless one.</summary>
    [Fact]
    public void The_search_tool_advertises_an_output_schema()
    {
        var tool = ToolCatalog.All.Single(t => t.ProtocolTool.Name == "pnp_search_commands");
        var schema = tool.ProtocolTool.OutputSchema;

        Assert.NotNull(schema);

        var properties = schema.Value.GetProperty("properties");
        Assert.True(properties.TryGetProperty("commands", out _), "The schema does not describe the result set.");
        Assert.True(properties.TryGetProperty("count", out _));

        // Inferred from the return type alone this would describe a string, which is the trap #12 names.
        Assert.Equal("object", schema.Value.GetProperty("type").GetString());
    }

    /// <summary>The corpus and the vendored name list are generated from different sources; they must agree.</summary>
    [Fact]
    public void The_corpus_agrees_with_the_vendored_name_list()
    {
        var vendored = CommandIndex.Commands.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Both directions would be too strict -- the two sources are pinned at different times -- but a
        // large divergence means one of the generators is broken.
        var missing = CommandCorpus.Commands.Count(c => !vendored.Contains(c.Name));

        Assert.True(missing < 25, $"{missing} indexed cmdlets are absent from the vendored name list.");
    }
}
