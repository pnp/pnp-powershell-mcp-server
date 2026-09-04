using PnPPowerShell.MCPServer.Models;
using PnPPowerShell.MCPServer.Services;
using PnPPowerShell.MCPServer.Tools;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>
/// Negative controls for the honesty checks elsewhere in this suite.
///
/// Three review rounds in a row produced a test that passed for the wrong reason — a conditional
/// assertion a wrong answer satisfied, a substring that appeared for an unrelated reason, a scenario
/// that never reached the code it named. These assert that the checks themselves still bite: each one
/// builds the wrong answer deliberately and proves it would be caught.
/// </summary>
public class InvariantGuardTests
{
    private static CommandSearchResult Search(int matched, int listed, bool detailOmitted = false) =>
        new()
        {
            Query = "q",
            Matched = matched,
            DetailOmitted = detailOmitted,
            Commands = [.. Enumerable.Range(0, listed).Select(i => new CommandSearchHit
            {
                Name = $"Get-PnPThing{i}",
                Verb = "Get",
                Noun = $"PnPThing{i}",
                Synopsis = "does a thing",
                Parameters = ["Identity"],
            })],
        };

    private static SessionListResult Sessions(int total, int listed) =>
        new()
        {
            Total = total,
            Sessions = [.. Enumerable.Range(0, listed).Select(i => new SessionSummary
            {
                Id = $"session-{i}",
                Status = "idle",
                LastUsedUtc = DateTimeOffset.UnixEpoch,
            })],
        };

    // Derived, not stored: the sessions bug was a hand-set Truncated that stopped matching the list.
    [Theory]
    [InlineData(40, 6, true)]
    [InlineData(6, 6, false)]
    [InlineData(0, 0, false)]
    public void Search_truncation_follows_the_list_it_describes(int matched, int listed, bool expected)
    {
        var result = Search(matched, listed);

        Assert.Equal(listed, result.Count);
        Assert.Equal(expected, result.Truncated);
    }

    [Theory]
    [InlineData(40, 6, true)]
    [InlineData(2, 2, false)]
    [InlineData(0, 0, false)]
    public void Session_truncation_follows_the_list_it_describes(int total, int listed, bool expected)
    {
        var result = Sessions(total, listed);

        Assert.Equal(listed, result.Count);
        Assert.Equal(expected, result.Truncated);
    }

    /// <summary>A partial page must never be rendered as though it were the whole answer.</summary>
    [Fact]
    public void A_partial_search_page_states_the_number_that_matched()
    {
        var text = PnPPowerShellTools.RenderForTest(Search(matched: 40, listed: 6));

        Assert.Contains("40 cmdlet(s)", text, StringComparison.Ordinal);
        Assert.Contains("showing the first 6", text, StringComparison.Ordinal);

        // The negative control: the wrong answer the old renderer produced must not appear.
        Assert.DoesNotContain("6 cmdlet(s) for 'q', most relevant first", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_whole_search_page_does_not_claim_to_be_truncated()
    {
        var text = PnPPowerShellTools.RenderForTest(Search(matched: 6, listed: 6));

        Assert.Contains("6 cmdlet(s)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("showing the first", text, StringComparison.Ordinal);
        Assert.DoesNotContain("did not fit", text, StringComparison.Ordinal);
    }

    /// <summary>Dropping per-cmdlet detail is a separate fact from dropping cmdlets, and is said so.</summary>
    [Fact]
    public void Omitted_detail_is_reported_even_when_nothing_was_dropped()
    {
        var text = PnPPowerShellTools.RenderForTest(Search(matched: 1, listed: 1, detailOmitted: true));

        Assert.False(Search(matched: 1, listed: 1, detailOmitted: true).Truncated);
        Assert.Contains("omitted", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The clamp must bound a hostile value without corrupting a legitimate one.</summary>
    [Theory]
    [InlineData("https://contoso.sharepoint.com/sites/marketing", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Clamp_leaves_realistic_values_alone(string? value, bool expectClamped)
    {
        var clamped = OutputLimit.Clamp(value);

        Assert.Equal(value, clamped);
        Assert.False(expectClamped);
    }

    [Fact]
    public void Clamp_bounds_a_pathological_value()
    {
        var clamped = OutputLimit.Clamp(new string('u', 100_000));

        Assert.NotNull(clamped);
        Assert.True(clamped.Length <= 512 + 3, $"Clamp returned {clamped.Length} characters.");
        Assert.EndsWith("...", clamped, StringComparison.Ordinal);
    }

    /// <summary>A real SharePoint URL is longer than Echo allows, which is why Clamp exists separately.</summary>
    [Fact]
    public void Clamp_is_not_merely_Echo()
    {
        var url = "https://contoso.sharepoint.com/sites/" + new string('a', 200);

        Assert.Equal(url, OutputLimit.Clamp(url));
        Assert.NotEqual(url, OutputLimit.Echo(url));
    }
}
