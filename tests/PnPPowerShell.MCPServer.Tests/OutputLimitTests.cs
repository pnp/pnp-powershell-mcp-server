using PnPPowerShell.MCPServer.Services;

namespace PnPPowerShell.MCPServer.Tests;

public class OutputLimitTests
{
    private static string Lines(int count) =>
        string.Join('\n', Enumerable.Range(0, count).Select(i => $"row-{i:D6} some payload text here"));

    [Fact]
    public void Output_within_the_limit_is_returned_untouched()
    {
        using var _ = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", "5000");
        const string small = "just a little output";

        Assert.Equal(small, OutputLimit.Apply(small));
    }

    [Fact]
    public void Oversized_output_is_truncated_and_says_so()
    {
        using var _ = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", "4000");

        var result = OutputLimit.Apply(Lines(500));

        Assert.Contains("[output truncated:", result);
        Assert.Contains("characters omitted]", result);
        Assert.True(result.Length <= 4000, $"Result was {result.Length} chars; the cap should bound it.");
    }

    [Fact]
    public void The_head_is_kept_so_the_first_rows_survive()
    {
        using var _ = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", "4000");

        var result = OutputLimit.Apply(Lines(500));

        Assert.Contains("row-000000", result);
        Assert.DoesNotContain("row-000499", result);
    }

    [Fact]
    public void Truncated_output_warns_against_parsing_it_as_complete()
    {
        // A truncated JSON array is still syntactically tempting; the model must be told not to trust it.
        using var _ = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", "3000");

        var result = OutputLimit.Apply(Lines(500));

        Assert.Contains("not necessarily valid JSON", result);
        Assert.Contains("must not be", result);
    }

    [Fact]
    public void A_caller_supplied_hint_replaces_the_default()
    {
        using var _ = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", "3000");

        var result = OutputLimit.Apply(Lines(500), "Lower maxSamples.");

        Assert.Contains("Lower maxSamples.", result);
        Assert.DoesNotContain("-PageSize", result);
    }

    [Fact]
    public void Truncation_prefers_a_line_boundary()
    {
        using var _ = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", "4000");

        var result = OutputLimit.Apply(Lines(500));
        var kept = result[..result.IndexOf("[output truncated:", StringComparison.Ordinal)].TrimEnd();

        // Every surviving line should be whole, so nothing is split mid-token.
        Assert.All(kept.Split('\n'), line => Assert.Matches(@"^row-\d{6} some payload text here$", line.Trim()));
    }

    [Fact]
    public void The_limit_is_configurable()
    {
        var payload = Lines(500);

        using (var _ = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", "3000"))
        {
            Assert.Contains("truncated", OutputLimit.Apply(payload));
        }

        using (var _ = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", "1000000"))
        {
            Assert.Equal(payload, OutputLimit.Apply(payload));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("10")]
    [InlineData("1000")]
    public void A_nonsensical_limit_falls_back_to_the_default(string value)
    {
        // A tiny or invalid limit would leave only the truncation note, so it must be ignored.
        using var _ = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", value);

        Assert.Equal(50_000, OutputLimit.MaxChars);
    }

    [Fact]
    public void Null_and_empty_are_handled()
    {
        Assert.Equal(string.Empty, OutputLimit.Apply(null));
        Assert.Equal(string.Empty, OutputLimit.Apply(string.Empty));
    }

    [Fact]
    public void A_single_enormous_line_is_still_capped()
    {
        // No line break to cut at, so the character cap has to hold on its own.
        using var _ = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", "3000");

        var result = OutputLimit.Apply(new string('x', 100_000));

        Assert.Contains("truncated", result);
        Assert.True(result.Length <= 3000, $"Result was {result.Length} chars.");
    }
}

public class OutputLimitBoundTests
{
    [Theory]
    [InlineData(2000, null)]
    [InlineData(2000, "a short hint")]
    [InlineData(5000, "an unusually long caller hint that goes on and on describing exactly how to narrow the query in far more words than are strictly necessary for the purpose")]
    [InlineData(50_000, null)]
    public void The_response_never_exceeds_the_configured_cap(int limit, string? hint)
    {
        // The truncation note counts against the cap; without reserving room for it the returned
        // response was larger than the maximum the setting promises.
        using var _ = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", limit.ToString());

        var result = OutputLimit.Apply(new string('x', 500_000), hint);

        Assert.True(result.Length <= limit, $"Result was {result.Length} chars against a {limit} cap.");
    }

    [Fact]
    public void The_note_survives_even_when_the_hint_is_long()
    {
        using var _ = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", "2000");

        var result = OutputLimit.Apply(new string('x', 500_000), new string('h', 400));

        // Trimming must come out of the output, never out of the warning.
        Assert.Contains("[output truncated:", result);
        Assert.Contains("must not be", result);
    }
}

/// <summary>Covers the suffix reservation that keeps trailing material inside the cap.</summary>
public class OutputLimitSuffixTests
{
    private const string Suffix = "\n\nTIP: this trailing guidance must survive and still be counted.";

    [Fact]
    public void A_suffix_is_kept_when_the_body_is_truncated()
    {
        using var _ = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", "3000");

        var result = OutputLimit.Apply(new string('x', 100_000), null, Suffix);

        Assert.Contains("truncated", result);
        Assert.EndsWith(Suffix, result);
    }

    [Fact]
    public void A_suffix_is_kept_when_the_body_is_not_truncated()
    {
        using var _ = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", "3000");

        Assert.Equal("body" + Suffix, OutputLimit.Apply("body", null, Suffix));
    }

    [Theory]
    [InlineData(2000)]
    [InlineData(5000)]
    [InlineData(50_000)]
    public void The_suffix_counts_against_the_cap(int limit)
    {
        // Appending the suffix after capping was how the response used to exceed its own limit.
        using var _ = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", limit.ToString());

        var result = OutputLimit.Apply(new string('x', 500_000), null, Suffix);

        Assert.True(result.Length <= limit, $"Result was {result.Length} chars against a {limit} cap.");
    }

    [Fact]
    public void An_error_hint_is_preserved_and_counted()
    {
        using var _ = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", "2000");
        var failure = "Error: " + new string('x', 100_000) + " (403) Forbidden";

        var hint = PnPErrorHints.HintFor(failure);
        var result = OutputLimit.Apply(failure, null, hint);

        Assert.NotNull(hint);
        Assert.Contains("Likely cause:", result);
        Assert.True(result.Length <= 2000, $"Result was {result.Length} chars.");
    }

    [Fact]
    public void HintFor_returns_null_for_successful_output()
    {
        Assert.Null(PnPErrorHints.HintFor("[{\"Url\":\"https://contoso.sharepoint.com\"}]"));
    }

    [Fact]
    public void Enrich_still_matches_HintFor()
    {
        const string failure = "Error: Get-PnPWeb: You are not signed in.";

        Assert.Equal(failure + PnPErrorHints.HintFor(failure), PnPErrorHints.Enrich(failure));
    }
}
