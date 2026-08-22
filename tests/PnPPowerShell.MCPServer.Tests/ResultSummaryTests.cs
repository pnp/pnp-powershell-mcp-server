using PnPPowerShell.MCPServer.Services;
using System.Text.Json;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>Summarising has to beat truncation on the one thing that matters: the answer stays parseable.</summary>
public class ResultSummaryTests
{
    private static string Rows(int count) =>
        "[" + string.Join(",", Enumerable.Range(0, count).Select(i =>
            $$"""{"Title":"List {{i}}","ItemCount":{{i}},"Url":"/sites/x/lists/{{i}}"}""")) + "]";

    [Fact]
    public void Captures_an_array_with_its_field_names()
    {
        var held = ResultSummary.TryCapture(Rows(5));

        Assert.NotNull(held);
        Assert.Equal(5, held.Rows.Count);
        Assert.Equal(["Title", "ItemCount", "Url"], held.Fields);
    }

    [Theory]
    [InlineData("""{"Title":"one object"}""")]
    [InlineData("Command completed successfully (no output).")]
    [InlineData("[not json")]
    [InlineData("""[{"Title":"only one row"}]""")]
    public void Declines_anything_it_cannot_page(string output) =>
        Assert.Null(ResultSummary.TryCapture(output));

    [Fact]
    public void Reports_the_true_total_rather_than_what_it_returned()
    {
        using var cap = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", "2000");

        var held = ResultSummary.TryCapture(Rows(500))!;
        var page = ResultSummary.Render(held, 0, "default");

        Assert.Contains("500 rows", page, StringComparison.Ordinal);
        Assert.DoesNotContain(OutputLimit.TruncationMarker, page, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_page_is_valid_json_which_is_the_whole_point()
    {
        using var cap = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", "2000");

        var held = ResultSummary.TryCapture(Rows(500))!;
        var offset = 0;
        var seen = 0;

        while (true)
        {
            var page = ResultSummary.Render(held, offset, "default");
            var array = page[page.IndexOf('[')..(page.LastIndexOf(']') + 1)];

            using var parsed = JsonDocument.Parse(array);
            seen += parsed.RootElement.GetArrayLength();

            if (!page.Contains("MORE:", StringComparison.Ordinal))
            {
                break;
            }

            // Advance the way a caller does, from the printed offset: adding the row count would spin
            // forever on a page that returned none.
            offset = int.Parse(System.Text.RegularExpressions.Regex.Match(page, @"offset (\d+)").Groups[1].Value);
        }

        Assert.Equal(500, seen);
    }

    [Fact]
    public void Names_the_cursor_and_the_next_offset()
    {
        using var cap = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", "2000");

        var held = ResultSummary.TryCapture(Rows(500))!;
        var page = ResultSummary.Render(held, 0, "reporting");

        Assert.Contains($"cursor '{held.Cursor}'", page, StringComparison.Ordinal);
        Assert.Contains("session 'reporting'", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Says_so_when_the_whole_set_fits_on_one_page()
    {
        var page = ResultSummary.Render(ResultSummary.TryCapture(Rows(3))!, 0, "default");

        Assert.Contains("COMPLETE:", page, StringComparison.Ordinal);
        Assert.DoesNotContain("MORE:", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Skips_a_row_wider_than_a_page_rather_than_emitting_a_fragment()
    {
        using var cap = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", OutputLimit.MinimumMaxChars.ToString());

        var huge = "[" + string.Join(",", Enumerable.Range(0, 3).Select(i => $$"""{"Big":"{{new string('x', 5_000)}}","I":{{i}}}""")) + "]";
        var held = ResultSummary.TryCapture(huge)!;
        var page = ResultSummary.Render(held, 0, "default");

        Assert.Contains("wider than a page", page, StringComparison.Ordinal);
        Assert.Contains("[]", page, StringComparison.Ordinal);

        // It still has to advance, or the caller is stuck on the oversized row forever.
        Assert.Contains("offset 1", page, StringComparison.Ordinal);
    }

    /// <summary>The guarantee the whole class exists for: a page is never handed to the caller truncated.</summary>
    [Theory]
    [InlineData(2_000, 40, 300)]
    [InlineData(2_000, 3, 60_000)]
    [InlineData(50_000, 500, 120)]
    [InlineData(2_000, 2, 1_900)]
    public void A_rendered_page_always_survives_the_output_cap(int cap, int rowCount, int rowWidth)
    {
        using var limit = new EnvVar("PNP_MCP_MAX_OUTPUT_CHARS", cap.ToString());

        var raw = "[" + string.Join(",", Enumerable.Range(0, rowCount)
            .Select(i => $$"""{"I":{{i}},"Pad":"{{new string('x', rowWidth)}}"}""")) + "]";

        var held = ResultSummary.TryCapture(raw)!;

        for (var offset = 0; offset < rowCount; offset++)
        {
            var page = ResultSummary.Render(held, offset, "default");
            var applied = OutputLimit.Apply(page);

            Assert.True(page.Length <= OutputLimit.MaxChars,
                $"Page at offset {offset} is {page.Length} characters against a {OutputLimit.MaxChars} cap.");
            Assert.DoesNotContain(OutputLimit.TruncationMarker, applied, StringComparison.Ordinal);

            using var parsed = JsonDocument.Parse(applied[applied.IndexOf('[')..(applied.LastIndexOf(']') + 1)]);
            Assert.Equal(JsonValueKind.Array, parsed.RootElement.ValueKind);
        }
    }

    [Fact]
    public void Clamps_an_offset_past_the_end()
    {
        var page = ResultSummary.Render(ResultSummary.TryCapture(Rows(4))!, 999, "default");

        Assert.Contains("Rows 4-4 of 4", page, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cursor_is_reachable_only_from_the_session_that_holds_it()
    {
        var manager = new PowerShellSessionManager();
        var session = manager.Get("reporting");
        session.Held = ResultSummary.TryCapture(Rows(4));

        Assert.Equal("reporting", manager.FindHolder(session.Held!.Cursor)?.Id);
        Assert.Null(manager.FindHolder("not-a-cursor"));
        Assert.Null(manager.FindHolder(null));
    }
}
