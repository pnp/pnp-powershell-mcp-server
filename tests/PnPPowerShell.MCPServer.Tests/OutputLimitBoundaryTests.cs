using System.Globalization;
using System.Text.RegularExpressions;
using PnPPowerShell.MCPServer.Services;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>The cap must hold for every input, including hostile suffixes and hints.</summary>
public class OutputLimitBoundaryTests
{
    [Theory]
    [InlineData(2000)]
    [InlineData(5000)]
    [InlineData(50_000)]
    public void A_suffix_larger_than_the_cap_cannot_break_the_bound(int limit)
    {
        using var _ = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", limit.ToString());

        var result = OutputLimit.Apply(new string('x', 500_000), null, new string('s', limit * 2));

        Assert.True(result.Length <= limit, $"Result was {result.Length} chars against a {limit} cap.");
    }

    [Fact]
    public void A_small_body_with_a_huge_suffix_stays_inside_the_cap()
    {
        // The early-return path skipped the bound check entirely.
        using var _ = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", "2000");

        var result = OutputLimit.Apply("tiny body", null, new string('s', 10_000));

        Assert.True(result.Length <= 2000, $"Result was {result.Length} chars.");
    }

    [Fact]
    public void An_enormous_narrowing_hint_cannot_break_the_bound()
    {
        using var _ = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", "2000");

        var result = OutputLimit.Apply(new string('x', 500_000), new string('h', 20_000));

        Assert.True(result.Length <= 2000, $"Result was {result.Length} chars.");
    }

    [Theory]
    [InlineData(2000, 0)]
    [InlineData(4000, 600)]
    [InlineData(50_000, 0)]
    public void The_omitted_count_matches_what_was_actually_dropped(int limit, int hintLength)
    {
        using var _ = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", limit.ToString());
        var body = string.Join('\n', Enumerable.Range(0, 20_000).Select(i => $"row-{i:D6}"));
        var hint = hintLength == 0 ? null : new string('h', hintLength);

        var result = OutputLimit.Apply(body, hint);

        var match = Regex.Match(result, @"\[output truncated: ([\d,]+) of ([\d,]+) characters omitted\]");
        Assert.True(match.Success, "No truncation note was produced.");

        var stated = int.Parse(match.Groups[1].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
        var total = int.Parse(match.Groups[2].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
        var kept = result[..result.IndexOf("\n\n[output truncated:", StringComparison.Ordinal)].Length;

        Assert.Equal(body.Length, total);
        Assert.Equal(body.Length - kept, stated);
    }
}
