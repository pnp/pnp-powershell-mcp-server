using System.Globalization;
using System.Text.RegularExpressions;
using PnPPowerShell.MCPServer.Models;
using PnPPowerShell.MCPServer.Services;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>The cap must hold for every input, including hostile suffixes and hints.</summary>
public class OutputLimitBoundaryTests
{
    /// <summary>The text-only fallback's notice counts against the cap, and claims completeness only when nothing was cut.</summary>
    [Theory]
    [InlineData(2000, false)]
    [InlineData(2000, true)]
    [InlineData(5000, false)]
    [InlineData(5000, true)]
    [InlineData(50_000, false)]
    [InlineData(50_000, true)]
    public void A_result_sent_as_text_alone_stays_inside_the_cap(int limit, bool list)
    {
        using var _ = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", limit.ToString(CultureInfo.InvariantCulture));

        // Each length is too long to carry twice, so every one takes the fallback.
        foreach (var length in new[] { (limit / 2) + 1, limit - 100, limit, limit * 3 })
        {
            string Render(SetupResult _) => OutputLimit.Apply(new string('x', length));
            var value = new SetupResult { Allowed = true, Installed = false, Command = "Install-Module PnP.PowerShell" };

            var result = list
                ? StructuredResult.FitToCap([1], (_, _) => value with { }, ToolOutputJsonContext.Default.SetupResult, Render)
                : StructuredResult.From(value with { }, ToolOutputJsonContext.Default.SetupResult, Render);
            var text = ToolResults.Text(result);

            Assert.Null(result.StructuredContent);
            Assert.True(text.Length <= limit, $"length {length}: {text.Length} characters against a {limit} cap.");
            Assert.Contains("structured output omitted", text, StringComparison.Ordinal);

            var whole = text.StartsWith(Render(value), StringComparison.Ordinal);
            Assert.True(whole || !text.Contains("Everything it carried is stated above", StringComparison.Ordinal), $"length {length}: claims completeness after cutting the text.");
            Assert.True(whole || text.Contains(OutputLimit.TruncationMarker, StringComparison.Ordinal), $"length {length}: cut the text without saying so.");
        }
    }

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
